// compositor.js — Phase 5 graph evaluator.
//
// On load:
//   1. Parse ?layer=<id> from URL.
//   2. GET /api/layer/<id> → Layer JSON.
//   3. Resize canvas to layer.resolution.
//   4. Open WS /hud/<id>; on RUN_TRIGGER, evaluate the matching widget's trigger graph
//      and render its result into the widget's rect.
//
// Trigger lifecycle:
//   • RUN_TRIGGER carries an optional waitId. The browser echoes it back in VISUAL_COMPLETE
//     so Hub's Bus._pendingWaits resolves the matching script's wait_for_visual.
//   • Completion timing is graph-authored: if the trigger graph contains a Visual.Complete
//     sink, the evaluator visits it after Display and that visit IS the completion signal.
//     If the graph has no Visual.Complete, the engine falls back to firing VISUAL_COMPLETE
//     immediately after the Display render so simple graphs still resolve.
//   • Idle loop (manifesto §4.5): when a non-onStartup trigger completes, the widget
//     immediately re-renders its onStartup graph so the canvas returns to its idle state.
//
// Hot reload:
//   • LAYER_RELOADED → location.reload(). The Hub LayerWatcher fires this when a .phxlayer
//     is saved; the page reboots and re-fetches /api/layer/<id> on next load.
//
// The evaluator walks each per-trigger graph upstream from the Display sink, lazily
// pulling on connected sockets and memoising visited nodes per render. Image, scalar,
// and vector node types are implemented in the kernel; unsupported nodes log a console
// warning and resolve to a transparent fallback.

(function () {
    'use strict';

    // ── Sprint 71 — kernel canvas pool ────────────────────────────────────
    // Several render kernels allocate a per-call HTMLCanvasElement via
    // document.createElement('canvas') for scratch work — masks, filters,
    // rotated-redraw stages, etc. With ~25 such sites in this file, a
    // 60 fps trigger creates and abandons hundreds of canvases per second
    // for the GC to reclaim, which is the suspected root cause of the OBS
    // browser-source memory creep noted in TODO.md.
    //
    // The pool below recycles canvases between render calls. Two callers:
    //   • Scratch site — confident the canvas does NOT escape the kernel
    //     (used as a local buffer, not returned via `value.image`):
    //       const c = canvasPool.acquire(w, h);
    //       ... draw ...
    //       canvasPool.release(c);
    //   • Escape site — kernel returns the canvas via `value.image` to a
    //     downstream consumer. Sprint 7 closed the leak by tracking these
    //     per-Evaluator: kernels call `this.acquireEscape(w, h)` and the
    //     Evaluator releases the whole batch in renderWidgetTrigger's
    //     finally clause, AFTER the Display sink's ctx.drawImage has
    //     painted the canvas into the visible context. The previous
    //     "release at end of render epoch" race is avoided because the
    //     release runs after the consumer's read completes, on the same
    //     synchronous tail of the trigger.
    //
    // Pool is keyed by `width|height` exact match — resizing a recycled
    // canvas via `.width = w; .height = h;` clears its bitmap (HTML
    // canvas semantics) which costs a full GPU upload, so reusing exact
    // sizes wins. Capped at 32 canvases / 16 MB total to bound RAM during
    // bursts; entries past the cap are dropped (the GC reclaims them as
    // before, no behavioural change).
    const canvasPool = (function () {
        const buckets = new Map(); // "WxH" → array of HTMLCanvasElement
        let totalKept = 0;
        const MAX_KEPT      = 32;
        const MAX_TOTAL_PX  = 16 * 1024 * 1024; // 16 MP cap (~64 MB at RGBA8)
        let totalPx = 0;

        function key(w, h) { return w + 'x' + h; }

        function acquire(w, h) {
            const k = key(w | 0, h | 0);
            const arr = buckets.get(k);
            if (arr && arr.length > 0) {
                const c = arr.pop();
                totalKept--;
                totalPx -= c.width * c.height;
                // Clear before returning so callers see a fresh-looking canvas.
                const ctx = c.getContext('2d');
                if (ctx) {
                    ctx.globalAlpha = 1;
                    ctx.globalCompositeOperation = 'source-over';
                    ctx.filter = 'none';
                    ctx.setTransform(1, 0, 0, 1, 0, 0);
                    ctx.clearRect(0, 0, c.width, c.height);
                }
                return c;
            }
            const c = document.createElement('canvas');
            c.width  = w | 0;
            c.height = h | 0;
            return c;
        }

        function release(c) {
            if (!c || !c.width || !c.height) return;
            if (totalKept >= MAX_KEPT) return;
            if (totalPx + c.width * c.height > MAX_TOTAL_PX) return;
            const k = key(c.width, c.height);
            let arr = buckets.get(k);
            if (!arr) { arr = []; buckets.set(k, arr); }
            arr.push(c);
            totalKept++;
            totalPx += c.width * c.height;
        }

        // V14 — the pool exposes acquire/release ONLY. It used to also expose a
        // stats() telemetry hook whose comment claimed "the debug overlay surfaces
        // this"; there is no debug overlay and stats() had no caller in this file or
        // anywhere else, so it was deleted rather than left as a promise. If pool
        // telemetry is ever wanted, totalKept / totalPx / buckets.size are still
        // right here — re-add a reader and a real surface in the same change.
        return { acquire, release };
    })();

    const params = new URLSearchParams(window.location.search);
    // Hub serves overlays under /layer/<id> (path form). The legacy convention reads
    // ?layer=<id> from the query — kept as a fallback so /?layer=main still works for
    // directly-loaded overlays. Path takes precedence so OBS sources configured with
    // /layer/vertical bind to the vertical layer instead of always falling back to 'main'.
    const pathMatch = window.location.pathname.match(/^\/layer\/([A-Za-z0-9_-]+)/);
    const layerId = (pathMatch && pathMatch[1]) || params.get('layer') || 'main';

    // Visualist preview surfaces (per-widget pane / popout) load the overlay with
    // ?widget=<widgetId>. The page then resizes the canvas to that widget's rect
    // and renders only that widget filling the canvas — so the preview pane sees
    // the widget's pixels at full size instead of a layer-scaled letterbox. OBS
    // browser sources never set this param, so production rendering is unaffected.
    const widgetFilterId = params.get('widget') || null;

    // ── Design-time vs production, and why one boolean decides it ────────────
    //
    // THE rule this whole file now turns on: a node's `PreviewText` mock may render ONLY at
    // design time. In production a live key with nothing paintable renders NOTHING.
    //
    // "Nothing paintable" is a narrow test on purpose — the key was never published, or its
    // value is JSON null (liveRenderableValue). A STALE key is paintable: it has a last known
    // value, and blanking it would turn a one-second producer hiccup into a visible flicker.
    // Freshness travels on the State / Live pins instead. See liveRenderableValue.
    //
    // Before the Overlay Live Channel, every tool reader fell back to PreviewText whenever
    // its Hub state was still empty — and the shipped Loyalty.Leaderboard default is
    // "1. viewer_one — 12,400 / 2. viewer_two — 9,830 / …". OBS's own recommendation is
    // "Shut down source when not visible", so the overlay reboots on EVERY scene return and
    // starts with empty state: those FAKE VIEWER NAMES painted on a live stream, several
    // times an hour, indistinguishable from real data to everyone watching. Blank is honest.
    //
    // The three design-time surfaces, in the order they were verified:
    //   • ?widget=<id>   — WidgetSinglePreviewPanel's per-widget preview / popout.
    //   • ?capture=1     — WidgetCanvasPreviewer's hidden thumbnail-capture WebView2.
    //   • ?client=editor — the whole-LAYER design surface.
    //     Visualist's LayerPreviewPanel appends it today (both the docked full-layer preview
    //     and the layer popout window, which hosts the same control) — it is the whole-layer
    //     surface, i.e. the one where layout work happens, and without the param every
    //     channel-reading widget rendered empty there because a bare /layer/<id> is
    //     byte-identical to what OBS loads.
    //
    //     REMAINING V6 ITEM: there is still no way to open a layer in a REAL browser as a
    //     design-time surface. The Inspector's Copy-OBS-URL is the only other producer of a
    //     /layer/<id> URL and it stays BARE — on purpose and permanently: that string is
    //     pasted into an OBS Browser Source, where ?client=editor would re-enable the
    //     PreviewText mocks on a live stream (fake viewer names on the leaderboard, in front
    //     of an audience). V6 therefore has to add a SEPARATE "open design preview in browser"
    //     affordance rather than teach Copy-OBS-URL a flag.
    //
    //     DO NOT delete this arm because a grep makes it look thinly used: it is the only
    //     design-time signal a surface that is not ?widget= and not ?capture= can send, and
    //     removing it would silently downgrade the layer preview back to blanks.
    const IS_DESIGN_TIME = widgetFilterId !== null
                        || params.get('client') === 'editor'
                        || params.has('capture');

    // ── Where PreviewText MOCKS may render ───────────────────────────────────
    //
    // Deliberately NARROWER than IS_DESIGN_TIME, and the difference is the whole
    // point. IS_DESIGN_TIME counts a bare `?widget=<id>` as design-time, but V6
    // established (see CLIENT_KIND below) that this exact URL is a SUPPORTED
    // PRODUCTION surface — "a streamer putting one widget in its own OBS Browser
    // Source at /layer/<id>?widget=alertbox, which is the obvious way to do that."
    // Hub classifies that socket Production; the page was still rendering mocks on
    // it, so the shipped Loyalty.Leaderboard default ("1. viewer_one — 12,400 …")
    // painted fake viewer names in front of an audience on every scene return
    // until the first live board arrived — precisely the fake-data-on-stream defect
    // V4 exists to remove, surviving on the one URL V6 insisted must keep working.
    //
    // A mock is therefore allowed only where the client ANNOUNCED itself as an
    // authoring surface: `?client=editor` (all three Visualist surfaces append it
    // unconditionally, so every genuine preview still mocks) or `?capture` (the
    // thumbnail rasteriser, which never faces an audience). Everything else —
    // including a bare `?widget=` — is treated as live and renders blank instead
    // of wrong, matching the "a blank beats a confident lie" rule the readers use.
    const MOCK_DATA_ALLOWED = params.get('client') === 'editor' || params.has('capture');

    // ── V6 bolt-on (landed here because compositor.js stays single-owner) ─────
    //
    // WHICH KIND of client this page is, announced to Hub on the SOCKET url. Hub's
    // /hud/<layerId> upgrade carried no discriminator, so every connection looked identical
    // — a live OBS Browser Source, the per-widget preview pane, the hidden thumbnail-capture
    // WebView2 and the whole-layer design surface all registered the same way. A page query
    // param does NOT carry over: connectSocket builds its own URL from
    // window.location.host + the layer id, so the marker has to be appended there
    // explicitly. That missing append is the reason Hub's classifier (already landed:
    // HUDServer.ClassifyClientKind) has been reading every socket as Production.
    //
    // ★ TWO VALUES ONLY, and the vocabulary is NOT free to invent — it has to be one
    // ClassifyClientKind already recognises, because that half is committed:
    //   'editor' → LayerClientKind.Editor      (its `client == "editor"` arm)
    //   'obs'    → LayerClientKind.Production  (its deliberate fail-toward-production arm,
    //              the same arm that keeps an OLD CACHED compositor.js sending no param at
    //              all classified as production — a stale browser source must never become
    //              invisible to presence)
    // The trap avoided here: a finer 'widget' / 'capture' vocabulary reads as Production,
    // because ClassifyClientKind's widget/capture arms test for those KEYS in the query, not
    // for a client VALUE. A per-widget preview would then have kept acking VISUAL_COMPLETE
    // against a script's wait_for_visual — exactly the bug V6 exists to fix. Hub's enum is
    // two-valued anyway, so there is nothing a finer split could feed.
    //
    // ★ Read from the EXPLICIT declaration only — deliberately NOT derived from
    // IS_DESIGN_TIME, even though sharing one predicate looks tidier.
    //
    // IS_DESIGN_TIME is also true for `?widget=` and `?capture`, and those mean something much
    // weaker: "a design-time surface may render PreviewText here". Feeding them to the WIRE
    // marker conflates that with "this socket is not a real viewer" — which post-V6 strips the
    // socket from layer presence entirely, so every script visual fast-succeeds and the source
    // renders NOTHING.
    //
    // The concrete casualty: a streamer putting one widget in its own OBS Browser Source at
    // /layer/<id>?widget=alertbox, which is the obvious way to do that. Derived, it announced
    // 'editor', got stripped from presence, and sat dark for a whole stream while showing green
    // in OBS. That URL worked before V6. A mock rendering on a preview is cosmetic; a live
    // source that never renders is not.
    //
    // Narrowing costs nothing: all three Visualist surfaces append client=editor
    // unconditionally, so every genuine preview still classifies. The page-side triad keeps its
    // three markers for the PreviewText question, where a false positive is harmless. Everything
    // else — including an OLD CACHED compositor.js that sends no param at all — must fail toward
    // 'obs', because a stale browser source must never become invisible to presence.
    const CLIENT_KIND = params.get('client') === 'editor' ? 'editor' : 'obs';

    // ── V13 A3 — the per-layer /hud connect token ────────────────────────────
    //
    // V13 opens the first UPWARD paths on the /hud socket that Hub does more than acknowledge:
    // VISUAL_COMPLETE now carries an author-authored `payload` that lands in a script's
    // `global._wait_payload`, and DEBUG_WIDGET_NODE reaches the Visualist editor. That socket
    // has no origin check and never had a credential, so those paths need an authorship
    // signal. Hub mints a per-layer token, embeds it in the HTML it serves for /layer/<id>,
    // and classifies each socket Trusted / Untrusted from what comes back — it does NOT refuse
    // the connection.
    //
    // ★ THE NAME IS `phx-hud-token`, and it is FIXED by the contract on both sides. V13's first
    // attempt read `phx-layer-token` here while Hub stamped `phx-hud-token`: the token was
    // permanently empty, every socket classified Untrusted, and both new capabilities were dead
    // with no error anywhere. The guard is
    // V13PayloadAndTokenTests.A1_ConnectTokenMetaName_IsTheSameLiteralOnBothSidesOfTheWire,
    // which re-reads THIS selector out of this file's source text and compares it to the literal
    // Hub stamps — so renaming one side alone fails the suite.
    //
    // ★ THE TOKEN IS STABLE ACROSS HUB RESTARTS (§8.3 as amended). Nothing here ever re-fetches
    // the page HTML — socket.onclose reconnects the SOCKET only, and LAYER_RELOADED prefers
    // softReloadLayer(), which re-fetches /api/layer/<id>. So a per-start token would leave every
    // OBS source that was open across a restart presenting a dead value for the rest of that
    // page's life, and every auto-update is a restart. Hub therefore persists it per layer. The
    // token proves "Hub served this page", not "this session is fresh".
    //
    // ★ IT HAS TO BE A META TAG, and that is forced by our own CSP rather than being a style
    // preference. The /layer/<id> response ships `script-src 'self' 'unsafe-eval'` with NO
    // 'unsafe-inline', so an injected <script> assigning a global would be blocked by the
    // browser and the token would silently never arrive — a whole overlay quietly demoted to
    // Untrusted with no error anywhere. A <meta> is inert content and passes untouched.
    //
    // ★ ABSENT IS LEGAL AND MUST STAY LEGAL. An OBS Browser Source can be running an
    // index.html / compositor.js pair cached from before this shipped, so there is no token in
    // its DOM at all. Then we connect ANYWAY, with no token parameter. A hard failure here
    // would black out a live overlay mid-stream on upgrade, with the only clue in a log nobody
    // is reading; an overlay that renders everything and declines two new privileged frames is
    // strictly better than one that disappears. For the same reason there is deliberately NO
    // retry, no re-read and no reconnect on a missing or rejected token — Hub logs once per
    // layer and the streamer fixes it with a browser-source cache refresh whenever they get to
    // it — or Hub asks this page for the ONE self-heal reload below. Read once at page scope: the
    // token cannot change without Hub re-serving the page.
    const CONNECT_TOKEN = (() => {
        try {
            const el = document.querySelector('meta[name="phx-hud-token"]');
            const t  = el && el.getAttribute('content');
            return t ? String(t) : '';
        } catch { return ''; }   // no document / hostile DOM — behave exactly as tokenless
    })();

    // ── V13 §8.3 belt-and-braces — the ONE self-heal hard reload ──────────────
    //
    // The grace path above is deliberate, but it is a DEGRADATION with no exit: nothing in this
    // file re-fetches the page HTML, so an Untrusted page stays Untrusted for its whole life —
    // its Visual.Complete payload silently empty for the rest of the stream. Hub may therefore
    // send HUD_RELOAD to a page that failed to prove provenance, which reloads /layer/<id> and
    // picks the token up.
    //
    // ★ ONCE PER PAGE, AND THE LATCH HAS TO LIVE HERE. A reload starts a brand-new page, so a
    // Hub-side "one per socket" latch dies with the socket the reload closes — the fresh page's
    // socket would be told again, reload again, forever. §8.3 is explicit that a reload storm on
    // a live overlay is worse than the degradation it is trying to fix. sessionStorage survives a
    // reload and is scoped to this browser-source session, which is exactly "once per page
    // lifetime". If storage is unavailable or blocked we do NOT reload at all: we cannot prove
    // once-only, and an unprovable once is a loop.
    const _SELF_HEAL_KEY = 'phx.hud.selfHealReloaded';
    function _selfHealHardReload(reason) {
        try {
            const ss = window.sessionStorage;
            if (!ss) return;                            // cannot prove once-only ⇒ never reload
            if (ss.getItem(_SELF_HEAL_KEY)) return;     // already spent this page's one attempt
            ss.setItem(_SELF_HEAL_KEY, '1');            // written BEFORE the reload, or it is moot
        } catch { return; }                             // storage blocked ⇒ never reload
        try { console.info('[compositor] self-heal reload:', reason || 'untrusted'); } catch { }
        try { window.location.reload(); } catch { }
    }

    // Track C — single-widget preview "active trigger". The embedded in-widget
    // preview (WidgetSinglePreviewPanel) sends SET_ACTIVE_TRIGGER whenever the
    // editor's active trigger tab changes, so the preview shows the trigger the
    // user is currently editing (keyframing) rather than the onStartup idle
    // state. Stays null in OBS / layer mode and for the initial paint, where
    // onStartup remains the correct default. Only consulted when widgetFilterId
    // is set (single-widget preview). See renderAll() and handleSetActiveTrigger().
    let previewActiveTrigger = null;

    // Three-state preview backdrop. Editor surfaces pass ?bg=black|gray|white so
    // semi-transparent widgets aren't judged against the implicit page background.
    // Default (param absent) is null — compositor.js paints nothing and OBS keeps
    // its existing transparent-over-page behaviour.
    const _bgRaw = (params.get('bg') || '').toLowerCase();
    const PREVIEW_BG = _bgRaw === 'white' ? '#ffffff'
                     : _bgRaw === 'gray'  ? '#808080'
                     : _bgRaw === 'black' ? '#000000'
                     : null;

    // P0 #2 — opt-in render telemetry. Append ?debug=1 to the overlay URL to
    // get console traces of every render-pipeline stage so "image not
    // transmitting" reports become actionable. No effect on production OBS
    // sources (default off).
    const DEBUG = params.has('debug');
    function debugLog(stage, payload) {
        if (!DEBUG) return;
        try { console.info(`[compositor] ${stage}`, payload); } catch { /* console disabled */ }
    }
    const statusEl = document.getElementById('status');
    // The status badge is hidden by default in index.html (it bleeds into OBS
    // browser sources otherwise). Reveal it only when ?debug=1 is set so
    // authors can still see the connection state during diagnostics.
    if (statusEl && DEBUG) statusEl.style.display = 'block';
    const canvas = document.getElementById('layer');
    const ctx = canvas.getContext('2d');
    // LOGICAL canvas dimensions (the layer/widget resolution, in CSS px). The
    // canvas BACKING STORE is sized logical×devicePixelRatio for crisp HiDPI
    // rendering (see applyResolution) and ctx is pre-scaled by dpr, so every
    // draw call below keeps working in LOGICAL coordinates. Anything that needs
    // the canvas dimensions as a COORDINATE must read logicalW/logicalH, NOT
    // canvas.width/height (which now hold device pixels). When dpr === 1 these
    // equal canvas.width/height exactly — i.e. byte-identical to the pre-dpr
    // behaviour for OBS at native resolution and 100%-scale monitors.
    let logicalW = canvas.width;
    let logicalH = canvas.height;

    // Cached on-screen canvas rect. getBoundingClientRect() forces a synchronous
    // style/layout recalculation, and _alignDomOverlayContainer measures once per
    // DOM-track sink (WebOverlay.Custom / Player.Embed) per rendered frame — so a
    // widget that ALSO carries an
    // animated source (Video.Load / animated .gif) runs that through the rAF
    // animator: frame N writes host.style.*, frame N+1 measures, 60x a second.
    // Textbook layout thrash on a live OBS source. The canvas only ever moves when
    // the viewport changes (#stage is a 100vw/100vh flex-centered absolute box) or
    // when applyResolution rewrites canvas.style.width/height, so we measure once
    // and invalidate at exactly those two points. #dom-overlay and #manipulator are
    // absolutely positioned, so mounting overlay hosts can't shift the canvas.
    let _canvasRectCache = null;
    function invalidateCanvasRect() { _canvasRectCache = null; }
    function canvasScreenRect() {
        if (!_canvasRectCache) {
            const r = canvas.getBoundingClientRect();
            // Snapshot into a plain object: the live DOMRect is a per-call allocation
            // and we want a stable object we can keep reading without re-measuring.
            _canvasRectCache = { left: r.left, top: r.top, width: r.width, height: r.height };
        }
        return _canvasRectCache;
    }

    let layer = null;        // Layer (Phoenix.Controls.Shared.Models.Layer)
    // Bounded LRU. Map keeps insertion-order; we promote on get and evict the
    // oldest entry when we exceed IMAGE_CACHE_MAX. Without this, multi-hour
    // streams that cycle through alert images, banners, etc. grow this Map
    // unboundedly and the OBS browser source's heap walks toward OOM.
    const IMAGE_CACHE_MAX = 256;
    let imageCache = new Map(); // path|url → HTMLImageElement
    let socket = null;

    // Overlay Live Channel — the ONE channel every live value rides on. It REPLACED the four
    // bespoke per-tool state objects that used to live here (captionState / timerState /
    // loyaltyState / counterState, each fed by its own CAPTION_UPDATE / TIMER_UPDATE /
    // LOYALTY_UPDATE / COUNTER_UPDATE broadcast): Hub retired those producers, so the objects
    // had no writer left and every reader now resolves keys out of this one Map.
    // Hub's OverlayLiveStore answers our LIVE_HELLO with a LIVE_SNAPSHOT and then pushes
    // coalesced LIVE_PATCH deltas; both land here.
    //   entries   — key → { v, s }: v = last published value (any JSON value; null is a legal
    //               value), s = Hub's liveness verdict for that key, 'active' or 'stale'.
    //               A missing key IS "missing" — that state never ships as an entry.
    //   seq       — the store's monotonic sequence for the newest frame we applied.
    //   helloSent — latches once we have announced a subscription, so sendLiveHello can
    //               tell "first announcement" from "re-announce with an unchanged key set".
    // Written ONLY by the LIVE_SNAPSHOT / LIVE_PATCH arms in onMessage and sendLiveHello.
    const liveState = { entries: new Map(), seq: 0, helloSent: false };

    /// Adapts one wire entry ({ k, v, s }) to the { v, s } shape liveState.entries stores.
    ///
    /// The liveness verdict is Hub's to make and cannot be recomputed here: the obvious
    /// browser-side design (ship LastWriteUtc + the expected interval, let our own clock
    /// decide) breaks against the store's coalescing, because a producer republishing an
    /// identical value refreshes LastWriteUtc WITHOUT shipping a frame — our copy would age
    /// and we would declare a perfectly healthy key stale.
    ///
    /// A frame that carries no `s` predates the liveness field: treat it as live. A missing
    /// verdict is not a stale verdict, and painting a working key as stale is the worse error.
    function _liveEntryOf(e) {
        return { v: e.v, s: e.s || 'active' };
    }

    /// The sequence number to judge an inbound live frame by. Hub stamps every frame with ONE
    /// globally monotonic counter at build time, so a LIVE_PATCH whose seq is BELOW the one we
    /// last applied is a reorder or a duplicate: applying it would overwrite newer values with
    /// older ones, so the patch arm drops it.
    ///
    /// ONLY the patch arm. A LIVE_SNAPSHOT is authoritative full state, not an increment, so it
    /// always applies and RESETS liveState.seq — the counter is process-static on the Hub side, so
    /// a Hub restart legitimately hands us a LOWER seq than we already hold, and guarding
    /// snapshots would leave the overlay permanently dark. The snapshot arm documents the full
    /// scenario.
    ///
    /// An EQUAL seq is NOT a duplicate and must not be dropped. The pump's periodic full resync
    /// re-ships the same values under the same seq when nothing has changed, and that resync is
    /// the only repair path for a frame the per-socket send channel silently evicted — dropping
    /// equal-seq frames would disable it.
    ///
    /// A frame carrying no usable seq is malformed rather than late, so it is still applied: we
    /// hand back the seq we already hold, which both passes the strictly-lower test and keeps
    /// such a frame from dragging liveState.seq backwards.
    function _liveFrameSeq(msg) {
        const n = Number(msg.seq);
        return Number.isFinite(n) ? n : liveState.seq;
    }

    // ── Overlay Live Channel — reading a key ─────────────────────────────────
    //
    // Every node reader goes through the four accessors below, so no two of them can
    // disagree about what "no data" means. Reserved tool roots are named once here rather
    // than re-spelled per reader: a typo in one of these strings is a permanently blank
    // widget with no error anywhere, which is precisely the failure mode the three-root
    // timer namespace exists to avoid.
    const LIVE_KEY_LOYALTY_BOARD      = 'loyalty.leaderboard';
    const LIVE_KEY_LOYALTY_CURRENCY   = 'loyalty.currency';
    const LIVE_KEY_CAPTION_ORIGINAL   = 'caption.original';
    const LIVE_KEY_CAPTION_TRANSLATED = 'caption.translated';

    // Shared empty board so a missing/null/malformed leaderboard allocates nothing per read.
    // (A STALE board is NOT one of those — it keeps its last rows; see liveRenderableValue.)
    const _NO_LEADERBOARD_ROWS = Object.freeze([]);

    /// The stored { v, s } for a key, or undefined when nothing has ever been published under
    /// it. Absence IS the Missing state — Hub never ships a "missing" entry.
    function liveEntry(key) {
        return (typeof key === 'string' && key) ? liveState.entries.get(key) : undefined;
    }

    // ── The two questions, answered by two DIFFERENT helpers ─────────────────
    //
    // "Is this data fresh?" and "is there a value I can paint?" are separate questions, and
    // one helper answering both is a bug rather than a convenience: a freshness verdict then
    // silently blanks a widget. liveStateOf answers freshness; liveRenderableValue answers
    // paintability; neither consults the other. Every reader below picks the one it needs.

    /// FRESHNESS — a key's liveness in the vocabulary the State / Live sockets expose:
    /// 'Active' / 'Stale' / 'Missing'. Hub owns the verdict (see _liveEntryOf for why it cannot
    /// be recomputed here), so this is a spelling change and nothing more.
    ///
    /// This is the ONLY route by which staleness reaches a canvas: it surfaces as the VALUE of a
    /// status pin the author can branch on, never as a suppressed render. A null-valued Active
    /// key still reports Active, because the key genuinely is being published — the emptiness of
    /// its value is liveRenderableValue's business, not this function's.
    function liveStateOf(key) {
        const e = liveEntry(key);
        if (!e) return 'Missing';
        return e.s === 'stale' ? 'Stale' : 'Active';
    }

    /// PAINTABILITY — the value of a key we are allowed to render, or undefined when there is
    /// none. Exactly TWO things collapse to undefined:
    ///   • the key was never published (Missing). Nothing has ever existed under it to paint,
    ///     and this is the case that killed the fake mock: production renders nothing (see
    ///     liveMock). That behaviour must not change.
    ///   • the value is JSON null. null is a legal published value, but it carries no
    ///     renderable content, and it is also what OverlayLiveStore degrades a NaN/±Infinity
    ///     publish to — so treating it as "no value" keeps a producer bug from rendering as
    ///     a confident 0.
    ///
    /// STALE IS DELIBERATELY PAINTABLE, and that is the entire reason this is split from
    /// liveStateOf. Hub's verdict is a ~3 s window (OverlayLiveStore.PumpIntervalMs 1000 ×
    /// StaleIntervalMultiplier 3) around producers that tick at 1 Hz and write through the ONE
    /// shared SQLite connection, so a WAL checkpoint or a GC pause is enough to overrun it on a
    /// perfectly healthy timer. Worse, the stale transition itself DIRTIES the key
    /// (RecomputeStaleTransitionsUnlocked runs before the pump takes its snapshot), so the
    /// verdict change is PUSHED as a patch and repaints immediately: folding staleness in here
    /// made a running subathon countdown visibly vanish and come back, where before the channel
    /// a missed producer beat caused no re-render at all. Serving the last known value is
    /// strictly better — the readout is a second or two old instead of absent — and a widget
    /// that wants to KNOW reads the verdict off its State / Live pin and can hide itself
    /// deliberately.
    function liveRenderableValue(key) {
        const e = liveEntry(key);
        if (!e || e.v === null) return undefined;
        return e.v;
    }

    /// A live value as display text. A JSON string yields its content, a number/bool its
    /// literal text, anything structured its compact JSON. Mirrors how overlay.get renders a
    /// value back into the script world, so the same key reads the same either side.
    function liveTextOf(v) {
        if (v === undefined || v === null) return '';
        if (typeof v === 'string')  return v;
        if (typeof v === 'number')  return Number.isFinite(v) ? String(v) : '';
        if (typeof v === 'boolean') return v ? 'true' : 'false';
        try { return JSON.stringify(v); } catch { return ''; }
    }

    /// A live value as a number — 0 on any failure, NEVER NaN.
    ///
    /// The coercion lives at the reader because that is where the author expressed intent by
    /// picking a pin type: overlay.publish stores every author value as a JSON string and
    /// deliberately does not sniff whether the text looks numeric, so "007" survives as
    /// "007". Tool keys keep their real JSON types, which makes this exact for them and
    /// best-effort for author strings. Number() rather than parseFloat() because the store's
    /// C# half parses with InvariantCulture and rejects trailing garbage — parseFloat would
    /// read "12abc" as 12 while Hub reads it as a failure.
    function liveNumberOf(v) {
        if (typeof v === 'number')  return Number.isFinite(v) ? v : 0;
        if (typeof v === 'boolean') return v ? 1 : 0;
        if (typeof v === 'string') {
            const s = v.trim();
            if (!s) return 0;
            const n = Number(s);
            return Number.isFinite(n) ? n : 0;
        }
        return 0;
    }

    /// The design-time-only PreviewText mock — '' in production, ALWAYS.
    ///
    /// This one function is the end of the fake-fallback bug. Every reader that used to do
    /// `if (noLiveData) return PreviewText` now calls this instead, so the mock reaches a
    /// canvas only on a surface MOCK_DATA_ALLOWED recognises. See that constant for why the
    /// gate is narrower than IS_DESIGN_TIME (a bare `?widget=` is a live OBS source, not a
    /// preview) and for the full account of why a mock on a live stream is worse than a blank.
    function liveMock(node) {
        if (!MOCK_DATA_ALLOWED) return '';
        const preview = stripQuotes(attr(node, 'PreviewText', ''));
        return (typeof preview === 'string' && preview) ? preview : '';
    }

    /// The `timer.<root>.` key prefix a Timer.Remaining / Countdown.Remaining /
    /// Stopwatch.Elapsed node reads. THE single normalisation point: liveKeysForNode
    /// subscribes `<this> + '*'` and evalTimerRemaining reads `<this> + '<field>'`, so the key
    /// we ask Hub for can never drift from the key we look up.
    ///
    /// An empty TimerName means "the default timer", which we cannot name: the slug is
    /// machine-generated (`t-<yyyy-MM-dd>-<letter>`) and we derive our subscription from
    /// literal attribute text BEFORE any frame arrives, so learning the default slug would
    /// require a frame we could only get by already being subscribed. Hub resolves that by
    /// mirroring the default timer's fields under the fixed `timer.__default.*` root, and by
    /// publishing every timer's fields a third time under its lower-cased DISPLAY NAME — which
    /// is what TimerName means everywhere else in the product. So one lower-cased lookup hits
    /// the slug root when the author typed a slug and the name root when they typed a name.
    ///
    /// trim().toLowerCase() must match TimerService's Trim().ToLowerInvariant() exactly. Two
    /// halves normalising differently is a blank widget with a running timer and a valid graph.
    function liveTimerRoot(node) {
        const raw = stripQuotes(attr(node, 'TimerName', ''));
        const name = (typeof raw === 'string' ? raw : '').trim().toLowerCase();
        return name ? `timer.${name}.` : 'timer.__default.';
    }

    /// The `counter.<name>.count` key a Counter.Value node reads, or '' when it names no
    /// counter. Lower-cased and deliberately NOT trimmed — CountersService.KeyName does
    /// exactly this, having dropped its Trim() precisely so both halves run one rule: a
    /// counter named " Deaths" must not publish counter.deaths.count while the widget
    /// subscribes "counter. deaths.count".
    function liveCounterKey(node) {
        const raw = stripQuotes(attr(node, 'Name', ''));
        const name = (typeof raw === 'string') ? raw : '';
        return name ? `counter.${name.toLowerCase()}.count` : '';
    }

    /// The literal key a Var.Live node binds. Trimmed ONLY because OverlayLiveStore.Norm
    /// trims on both the publish and the LIVE_HELLO path, so an untrimmed lookup would miss
    /// the entry Hub trimmed on our behalf. No case folding: keys are Ordinal on the Hub side
    /// and overlay.publish never folds, so `Boss_HP` and `boss_hp` are two different keys.
    ///
    /// "Literal" is the documented limit here: a computed key (`score_{user.name}`) is
    /// writable and readable by script, but unbindable, because the browser derives its
    /// subscription from attribute TEXT at graph-scan time.
    function liveVarKey(node) {
        const raw = stripQuotes(attr(node, 'Key', ''));
        return (typeof raw === 'string' ? raw : '').trim();
    }

    // ── V10 — the goal.* family, the channel's second RESERVED root ───────────
    //
    // ★ THE PRODUCER HAS LANDED. This is the browser READER half; Hub's GoalChannelProducer
    // publishes the follower / sub / bits / charity roots from Twitch's three channel-goal events
    // and its three charity-CAMPAIGN events. goal.tip.* is still unproduced — it belongs to C1's
    // donation ingestion, a SECOND publisher into this SAME family, which is what "one goal
    // model" means. Every publisher writes FOUR fields under one root:
    //
    //     goal.<kind>.current    number
    //     goal.<kind>.target     number
    //     goal.<kind>.progress   number, 0..1 clamped; 0 when target <= 0
    //     goal.<kind>.label      string
    //
    // <kind> is follower | sub | bits | tip | charity, or an author's custom_<slug>.
    //
    // The prefix and the field list are MIRRORS of NodeTemplates.GoalKeyPrefix /
    // NodeTemplates.GoalFields, and WidgetFamilyV10Tests pins both pairs. That test is not
    // ceremony: the publisher and this reader spelling one field differently produces a
    // permanently blank widget with a running producer, a valid graph and no error on either
    // side — the same failure mode the timer family's three-root namespace exists to avoid,
    // and it is undiagnosable from the overlay because a key nobody published is simply absent.
    const GOAL_KEY_PREFIX = 'goal.';
    const GOAL_FIELDS     = ['current', 'target', 'progress', 'label'];

    // The RESERVED kind vocabulary, mirrored from NodeTemplates.GoalKinds (WidgetFamilyV10Tests
    // pins the pair). It exists for exactly one purpose: liveGoalRoot case-folds a kind ONLY
    // when it is one of these. Everything else is an author slug and is used verbatim.
    const GOAL_RESERVED_KINDS = ['follower', 'sub', 'bits', 'tip', 'charity'];

    /// The `goal.<kind>.` root a Goal.Progress node reads, or '' when it names no kind.
    ///
    /// THE single normalisation point for this family, exactly as liveTimerRoot is for the
    /// timer family: liveKeysForNode subscribes `<this> + '*'` and evalGoalProgress reads
    /// `<this> + '<field>'`, so the key we ask Hub for can never drift from the key we look up.
    ///
    /// ★ CASE-FOLDING IS SCOPED TO THE RESERVED VOCABULARY, and the scope is the whole point.
    /// The Kind box is typed by hand, so "Follower" must not subscribe goal.Follower.* against a
    /// goal.follower.* publish — the five reserved kinds are therefore folded. But an author's
    /// custom_<slug> is a key THEY chose and publish literally: OverlayLiveStore matches Ordinal
    /// and its Norm() only trims, so folding the whole kind made custom_BossHP subscribe
    /// goal.custom_bosshp. while every publish landed on goal.custom_BossHP. — and the
    /// publisher-side subscription gate then dropped every write. Same rule liveVarKey states:
    /// trim, never fold, because the store never folds either.
    ///
    /// Not sanitised beyond that, for the same reason: the channel enforces no charset, so
    /// rewriting an unusual kind would silently read a different key than the author typed.
    ///
    /// The EMPTY result is load-bearing, not a defensive nicety: without it a blank Kind would
    /// build the roots 'current' / 'target' / 'progress' / 'label', which are legal author keys
    /// somebody may well have published with overlay.publish. An unconfigured node would then
    /// bind a stranger's data.
    function liveGoalRoot(node) {
        const raw = stripQuotes(attr(node, 'Kind', ''));
        const typed = (typeof raw === 'string' ? raw : '').trim();
        if (!typed) return '';
        const folded = typed.toLowerCase();
        const kind = GOAL_RESERVED_KINDS.indexOf(folded) >= 0 ? folded : typed;
        return `${GOAL_KEY_PREFIX}${kind}.`;
    }

    /// Liveness for a whole goal ROOT rather than for one nominated key — because a publisher
    /// is allowed to fill part of the family. The common custom_<slug> case publishes current
    /// and target only and lets the reader derive progress, so judging presence on a single
    /// field would report Missing for a goal that is visibly working on screen.
    ///
    ///   • any field of the root is Stale   → 'Stale'. Stale WINS. The fields are published
    ///     together, so a split verdict means the producer stopped mid-family — precisely the
    ///     condition this pin exists to report — and the readout genuinely is a beat old.
    ///   • any field present                → 'Active'
    ///   • nothing published under the root → 'Missing'
    ///
    /// Vocabulary note for authors: 'Stale' is only reachable if the goal publisher declares an
    /// ExpectedInterval. Hub's ComputeState cannot return Stale for a key that declared none,
    /// and goal updates are event-driven, so in practice this answers Active or Missing.
    function liveGoalState(root) {
        if (!root) return 'Missing';
        let seen = false;
        for (const field of GOAL_FIELDS) {
            const s = liveStateOf(root + field);
            if (s === 'Stale') return 'Stale';
            if (s === 'Active') seen = true;
        }
        return seen ? 'Active' : 'Missing';
    }

    /// A goal's 0..1 progress. A PUBLISHED progress always wins; the current/target derivation
    /// is a FALLBACK and never an override, so this reader and Hub's own publisher-side clamp
    /// cannot disagree about a goal that published all four fields.
    ///
    /// Every exit is a finite number inside [0,1]:
    ///   • target <= 0, or either side unpublished → 0. A zero target is the honest zero
    ///     (0 out of 0 is not "complete"), and 0 is what Hub's own clamp emits for it.
    ///   • the clamp is applied HERE as well as at the publisher because an author can publish
    ///     goal.<kind>.progress by hand with overlay.publish, and a stray 5 must not make a bar
    ///     500% wide.
    ///   • NaN can never escape. A NaN on a Scalar pin does not render as a blank — it
    ///     propagates through every downstream transform (a NaN width, a NaN translate, a NaN
    ///     lerp) and silently deletes unrelated parts of the widget, so it is stopped at source.
    ///     liveNumberOf already refuses NaN; the clamp is the second gate for the division.
    function goalProgressOf(publishedV, currentV, targetV) {
        if (publishedV !== undefined) return clampProgress01(liveNumberOf(publishedV));
        if (currentV === undefined || targetV === undefined) return 0;
        const target = liveNumberOf(targetV);
        if (!(target > 0)) return 0;
        return clampProgress01(liveNumberOf(currentV) / target);
    }

    /// 0..1 clamp that also swallows NaN / ±Infinity — see goalProgressOf for why a NaN
    /// reaching a Scalar socket is worse than a wrong number.
    function clampProgress01(n) {
        if (!Number.isFinite(n)) return 0;
        return n < 0 ? 0 : (n > 1 ? 1 : n);
    }

    // ── V10 — the channel ARRAY reader's key + row helpers ────────────────────

    /// The literal channel key a List.Live node binds. Same rule as liveVarKey: trimmed
    /// because OverlayLiveStore.Norm trims on both the publish and the LIVE_HELLO path, and
    /// NOT case-folded because keys are Ordinal on the Hub side. "Literal" is the documented
    /// limit — the subscription is derived from this attribute's TEXT at graph-scan time, so a
    /// computed key is publishable but never bindable.
    function liveListKey(node) {
        const raw = stripQuotes(attr(node, 'Key', ''));
        return (typeof raw === 'string' ? raw : '').trim();
    }

    // Shared empty result so a Missing / null / non-array list allocates nothing per read.
    // Separate from _NO_LEADERBOARD_ROWS purely so neither name lies about its owner; both are
    // frozen empties and the duplication costs one object for the life of the page.
    const _NO_LIST_ROWS = Object.freeze([]);

    // One-slot memo for the JSON-string arm below. JSON.parse is a pure function of its input,
    // so caching on the exact source string can never serve a wrong answer; two keys alternating
    // just parse every time, which is what happened before the memo existed. It matters because
    // liveListRows is called per PIN per render frame — a rotator reading Row/Value/Number off a
    // 50-row published list would otherwise re-parse it ~180 times a second.
    const _listStringMemo = { src: null, rows: _NO_LIST_ROWS };

    // Keys already reported as not holding a list, so the diagnostic fires ONCE per key instead of
    // at frame rate. Shared by all THREE not-a-list outcomes (see reportListNotArray below and
    // liveListRows' catch), not only by the JSON.parse failure it was originally added for.
    //
    // ★ THE LATCH IS NOW LOAD-BEARING FOR HUB, not just for the console. The report goes out as a
    // TRIGGER_DIAGNOSTIC frame (sendEvalDiagnostic) because a console.warn inside an OBS Browser
    // Source reaches nobody, and an un-latched frame from a per-pin-per-frame reader would be a
    // write to Hub's socket and a System Log line ~180 times a second. So the gate is not tidiness:
    // it is what makes routing the diagnostic to Hub safe at all.
    //
    // Dedupe is on the KEY, deliberately NOT on the source string. Two reasons, and the second is
    // the sharp one:
    //   • _listStringMemo is a ONE-slot memo shared by every key, so on a layer with two List.Live
    //     nodes — one of them holding a malformed string — the memo thrashes on every read and the
    //     parse re-runs per frame.
    //   • a producer republishing malformed rows every tick emits a DISTINCT string each time (a
    //     changing count, a timestamp), so a source-string latch would let a fresh string through
    //     on every publish — i.e. it would not bound anything. Key-scoped bounds it absolutely:
    //     one report per binding, whatever the publisher does.
    // Key-scoped is also the idiom Hub uses for exactly this shape (NoteProbeMissOnce per probe,
    // ReportHelloDeadline per layer): the first sighting is the whole diagnostic, and the author
    // does not need it repeated. The cost is that a SECOND, different typo on the same key is not
    // re-reported within one page — accepted, because the first report already names the binding to
    // go and look at.
    //
    // Bounded by construction: keys come from List.Live Key attributes in the loaded graph, so the
    // live set is a handful. The cap only guards a design-time session where the author retypes the
    // Key box while a soft reload re-scans the graph — past it the author already has 32 warnings.
    const _listParseWarned    = new Set();
    const _LIST_WARN_KEY_CAP  = 32;

    /// The once-per-key report for a List.Live key holding something that is not a list, for the
    /// two outcomes that CANNOT throw and therefore cannot ride the JSON.parse catch.
    ///
    /// ★ WHY THIS EXISTS. Only a THROWN parse error used to be reported. A published string that
    /// parses cleanly into an OBJECT — `{"ann":5}` where `[{"name":"ann"}]` was meant, which is the
    /// most natural publishing mistake of the lot — took the `Array.isArray(parsed)` false branch:
    /// no throw, no latch, no Hub frame, frozen empty returned. A stored value that is neither
    /// string nor array (a tool publishing a JSON number or object through the JsonNode overload)
    /// fell out of the tail `return _NO_LIST_ROWS` just as quietly. Both produce the IDENTICAL blank
    /// widget the "reported, not swallowed" rounds were filed to remove — State Active, Count 0,
    /// nothing rendered, nothing anywhere to read — so "reported" now means every not-a-list
    /// outcome rather than only the one that happens to raise.
    ///
    /// AND THE REASON CODE MUST NOT LIE ABOUT WHICH HAPPENED. One code for all three sent an author
    /// hunting a syntax error in text that parsed perfectly, so each caller passes its own code and
    /// its own `got=` detail: `list_not_json_array` (not JSON at all — the catch), this function's
    /// `list_json_not_array` (valid JSON, wrong shape) and `list_value_not_array` (never a string).
    ///
    /// The three properties the caller depends on are all preserved here. DEDUPE: on the KEY (see
    /// _listParseWarned for why not on the value), because a reader runs per pin per render frame
    /// and the non-string arm has no memo to short-circuit it — an un-gated frame would be a write
    /// to Hub's socket and a System Log line at frame rate. NO-THROW: sendEvalDiagnostic is
    /// no-throw by construction and the console line is wrapped, so nothing escapes into a render
    /// read. FROZEN EMPTY: this returns nothing at all — every caller still returns _NO_LIST_ROWS.
    ///
    /// The catch below keeps its own inline copy of this shape rather than calling here, because its
    /// message carries the parse-error text and the existing regression pins read the latch and the
    /// send out of the catch block itself.
    function reportListNotArray(key, widgetId, reason, detail, saw) {
        if (_listParseWarned.has(key) || _listParseWarned.size >= _LIST_WARN_KEY_CAP) return;
        _listParseWarned.add(key);
        sendEvalDiagnostic(reason, `key='${key}' expected=JSON-array ${detail}`, widgetId);
        try {
            console.warn(
                `[Visualist] List.Live: key '${key}' holds ${saw}, not a JSON array — rendering ` +
                `no rows. Publish a JSON array, e.g. ["a","b"] or ` +
                `[{"name":"ann","amount":5}].`);
        } catch (_) { /* console disabled */ }
    }

    /// The rows under a List.Live key, or [] when the key was never published, is null, or
    /// holds anything that is not a JSON array. A STALE list keeps its last rows — the
    /// staleness is reported on the State pin instead (see liveRenderableValue), so a list that
    /// stopped updating shows slightly old rows rather than emptying itself mid-stream.
    ///
    /// TWO ACCEPTED SHAPES, and the second one is what makes this node reachable from a script
    /// at all. A tool publishing through OverlayLiveStore's JsonNode overload stores a real JSON
    /// array (loyalty.leaderboard is the only one today). But the ONLY publish surface a .phx
    /// script can reach is overlay.publish → PublishString, which is string-only BY DESIGN — it
    /// never sniffs whether the text looks numeric or structured. So a streamer following this
    /// node's own tooltip and publishing rows from a script stored a STRING, an Array.isArray-only
    /// reader answered "not a list", and the result was a permanently blank widget with a running
    /// publisher, a valid graph and no error anywhere.
    ///
    /// The coercion therefore lives HERE, at the reader, which is where this file already puts it
    /// (see liveNumberOf's own rationale: the author declared their intent by picking a pin type,
    /// and dropping a List.Live node IS that declaration). Guarded three ways so it can only ever
    /// widen what is accepted: the parse is inside try/catch, the result is used only when
    /// Array.isArray passes, and a bare OBJECT is deliberately NOT promoted to a one-row list —
    /// that would be guessing at the author's shape rather than reading their declaration.
    ///
    /// ★ EVERY NOT-A-LIST OUTCOME IS REPORTED TO HUB, once per key — not only the parse throw. The
    /// parse stays non-throwing — a render read must never fault — but a catch that says nothing
    /// reproduces the very defect this arm removed: an unquoted key, a trailing comma or a
    /// single-quoted string is otherwise indistinguishable from "the key was never published"
    /// (State Empty, Count 0, nothing rendered, no error on either side). A near miss is in fact the
    /// likelier authoring mistake than a plain sentence, because the author who reached this arm was
    /// already trying to publish rows.
    ///
    /// The two SILENT siblings of that throw are the point of reportListNotArray: a string that
    /// parses into an object rather than an array raises nothing, and a stored value that is neither
    /// string nor array reaches no parse at all. Both used to exit with the frozen empty and no
    /// evidence anywhere. The one case that stays deliberately silent is `undefined`, because
    /// liveRenderableValue collapses "never published" (Missing — the normal state before a
    /// producer's first tick) and a legally published JSON null into it, and neither is a mistake.
    ///
    /// ★ AND A console.warn ALONE IS NOT "REPORTED". The production surface is an OBS Browser
    /// Source with no DevTools attached, and the widget-editor WebView2 preview has no reachable
    /// console at all, so a warn-only catch is silence with extra steps — the same blank widget,
    /// the same absent error. The report therefore rides sendEvalDiagnostic, which lands in Hub's
    /// System Log (reason `list_not_json_array` here, `list_json_not_array` / `list_value_not_array`
    /// from the two non-throwing arms), and the console.warn stays as the second surface for whoever
    /// DOES have DevTools open. Both name the key, so the author is pointed at the one binding to fix
    /// rather than at "a list somewhere".
    ///
    /// `widgetId` is threaded in purely so the Hub line can name the widget; it is optional and
    /// only ever decorates the diagnostic.
    function liveListRows(key, widgetId) {
        if (!key) return _NO_LIST_ROWS;
        const v = liveRenderableValue(key);
        if (Array.isArray(v)) return v;
        if (typeof v === 'string') {
            if (v === _listStringMemo.src) return _listStringMemo.rows;
            let rows = _NO_LIST_ROWS;
            // Set ONLY when the parse SUCCEEDED and produced something that is not an array; the
            // kind is carried rather than the value so the report can name what arrived.
            let parsedAs = '';
            try {
                const parsed = JSON.parse(v);
                if (Array.isArray(parsed)) rows = parsed;
                else parsedAs = (parsed === null ? 'null' : typeof parsed);
            } catch (e) {
                // Not JSON at all — the frozen empty is the honest answer and nothing throws out
                // of a render read. But SAY SO, once, and say it somewhere the streamer can
                // actually see. Reported BEFORE the memo store below, because that store is what
                // makes the repeat reads cheap and this the only pass that sees the failure.
                //
                // BOTH SURFACES sit INSIDE the latch — the Hub frame as much as the console line.
                // liveListRows runs per pin per render frame, so an un-gated frame would hammer
                // Hub's socket and its System Log at frame rate.
                if (!_listParseWarned.has(key) && _listParseWarned.size < _LIST_WARN_KEY_CAP) {
                    _listParseWarned.add(key);
                    const why = (e && e.message) ? e.message : String(e);
                    sendEvalDiagnostic(
                        'list_not_json_array',
                        `key='${key}' expected=JSON-array parse='${why}'`,
                        widgetId);
                    // Wrapped: this line sits inside a catch, so an unavailable console would
                    // otherwise throw straight out of a render read and take the pin with it.
                    try {
                        console.warn(
                            `[Visualist] List.Live: key '${key}' holds a string that is not a JSON ` +
                            `array — rendering no rows. Publish a JSON array, e.g. ` +
                            `["a","b"] or [{"name":"ann","amount":5}]. ` +
                            `Parse error: ${why}`);
                    } catch (_) { /* console disabled */ }
                }
            }
            // VALID JSON, WRONG SHAPE — an object where an array was meant. Nothing threw, so this
            // arm reached neither the catch above nor any other surface, and the author saw the same
            // blank widget a syntax error used to give them. Raised OUTSIDE the catch on purpose:
            // inside it, anything the report ever threw would be handled by the very catch that had
            // just finished, and the author would be told about a parse error that never happened.
            if (parsedAs) {
                reportListNotArray(key, widgetId, 'list_json_not_array',
                                   `parsed=${parsedAs}`, `valid JSON holding a ${parsedAs}`);
            }
            _listStringMemo.src  = v;
            _listStringMemo.rows = rows;
            return rows;
        }
        // NEITHER an array NOR a string — a tool that published a JSON number / bool / object under
        // a key a List.Live node binds. There is no memo on this arm, so the latch inside the report
        // is the only thing standing between a per-pin-per-frame reader and Hub's socket.
        //
        // `undefined` stays SILENT and that is deliberate, not an oversight: liveRenderableValue
        // collapses BOTH a never-published key (Missing — every overlay's state until its producer's
        // first tick) and a legally published JSON null into undefined, so reporting it would turn
        // every idle overlay into a System Log flood and bury the reports that mean something.
        if (v !== undefined) {
            reportListNotArray(key, widgetId, 'list_value_not_array',
                               `stored=${typeof v}`, `a JSON ${typeof v}`);
        }
        return _NO_LIST_ROWS;
    }

    /// One field of a list row, addressed by NAME, case-insensitively.
    ///
    /// A BARE row (string / number / bool — an array of relative paths for an emote wall, an
    /// array of names for a queue) has no fields, so it IS its own value and the Field
    /// attribute is ignored. That is what lets one node read both row shapes without the
    /// author declaring which kind of array they published.
    ///
    /// Returns undefined for "no such field", which the callers turn into '' / 0 — never the
    /// literal token text, because a leaked "{name}" on a live overlay reads as a broken
    /// widget rather than as missing data.
    function liveListField(row, field) {
        if (row === null || row === undefined) return undefined;
        if (typeof row !== 'object') return row;
        const want = (typeof field === 'string' ? field : '').trim().toLowerCase();
        if (!want) return undefined;
        for (const k of Object.keys(row)) {
            if (k.toLowerCase() === want) return row[k];
        }
        return undefined;
    }

    /// The leaderboard rows, or [] when the key is Missing (never published), null, or carries
    /// anything but an array. Hub publishes loyalty.leaderboard as a real JSON ARRAY of
    /// { rank, name, balance } rather than a pre-joined string, which is what lets a widget
    /// address one rank directly.
    ///
    /// A STALE board keeps its last rows — see liveRenderableValue. The staleness itself is
    /// reported by the State pin, so a board that stopped updating shows slightly old standings
    /// instead of emptying itself mid-stream.
    function liveLeaderboardRows() {
        const v = liveRenderableValue(LIVE_KEY_LOYALTY_BOARD);
        return Array.isArray(v) ? v : _NO_LEADERBOARD_ROWS;
    }

    /// One leaderboard row by viewer name, case-insensitive — how Loyalty.Balance resolves a
    /// user. Per-user balance keys are deliberately not published (one key per viewer who ever
    /// earned a point is unbounded), and the board already carries every name an overlay can
    /// show. A linear scan replaces the old per-frame `byName` index: the board is
    /// LeaderboardSize rows (10 by default) and now arrives per patch rather than per frame, so
    /// rebuilding an index would cost more than the scans it saves.
    function liveLeaderboardRow(user) {
        const needle = (typeof user === 'string' ? user : '').toLowerCase();
        if (!needle) return null;
        for (const row of liveLeaderboardRows()) {
            if (row && typeof row.name === 'string' && row.name.toLowerCase() === needle) return row;
        }
        return null;
    }

    /// The 0-based array position a Loyalty.Leaderboard's Rank/Name/Balance sockets address,
    /// from its 1-BASED `Index` attribute.
    ///
    /// 1-based deliberately, and it is the one place this file makes a semantic choice the C#
    /// template has to agree with: the board's rows carry a 1-based `rank`, and the same node
    /// exposes a Rank output, so an Index that reads as a rank ("Index 3 → Rank 3") is the only
    /// spelling in which those two can't contradict each other.
    ///
    /// A missing, non-numeric or sub-1 Index means first place — the same forgiving default the
    /// Size attribute takes. Out-of-range is NOT clamped; the caller renders '' / 0, because
    /// showing rank 1 where the author asked for rank 12 is a wrong answer dressed as a right one.
    function liveLeaderboardIndex(node) {
        const n = parseInt(stripQuotes(attr(node, 'Index', '1')), 10);
        return (Number.isFinite(n) && n >= 1) ? n - 1 : 0;
    }

    // L49 — Caption.LiveCaption emits two output sockets. The names live in C# (NodeTemplates)
    // and were previously hardcoded as inline string literals here, so any rename on the C# side
    // would silently fall through to the wrong branch. Lift them to a single source of truth.
    // BugFixSweep3_Visualist_JS_Tests asserts this constant is present.
    const CAPTION_SOCKETS = { ORIGINAL: 'Original', TRANSLATED: 'Translated' };

    // V7 — String.Select row capacity: the number of Case<i>/Value<i> attribute pairs the
    // template ships, in addition to the mandatory Default row. Must equal
    // NodeTemplates.StringSelectRows on the C# side or the browser would stop reading rows
    // the editor still lets an author fill in; DynamicMediaSourceV7Tests pins the pair.
    // TWELVE is derived, not picked: the Alerts tool labels ten families today plus a
    // generic fallback, so the eight-way fan-out every other node in the suite uses would
    // not fit the one graph this node exists to build.
    const STRING_SELECT_ROWS = 12;
    const _warnedUnknownCaptionSockets = new Set();

    // …and the spelling the TEMPLATE actually ships. NodeTemplates.cs declares
    // Caption.LiveCaption's outputs as { Text, Translated } — the untranslated stream's socket
    // is named "Text", not "Original". So every caption widget in existence reached its
    // original text through the unknown-socket branch below and logged the "unrecognized socket
    // name" warning once per page. Renaming the socket is not an option (a socket rename prunes
    // the links in every existing .phxlayer), so the accepted spellings live here.
    //
    // Kept OUT of CAPTION_SOCKETS on purpose: the caption key contract test derives the
    // published key set from that object's string literals, so it must stay exactly the two
    // entries that map to caption.original / caption.translated. Adding 'Text' there would
    // claim a caption.text key nobody publishes.
    const CAPTION_ORIGINAL_SOCKETS = new Set(['Text', CAPTION_SOCKETS.ORIGINAL]);

    // M64 — FontFace load tracking. We pay the `document.fonts.load` await once per
    // (family, size) pair so the first Text.Render measures with the actual face metrics
    // instead of the system fallback's. Subsequent renders short-circuit via this Set.
    const _loadedFontKeys = new Set();
    async function ensureFontLoaded(fontFamily, fontSize) {
        if (!fontFamily) return;
        if (typeof document === 'undefined' || !document.fonts) return;
        // Strip surrounding quotes so the Set key matches what we feed document.fonts.load.
        const fam = String(fontFamily).replace(/^["']|["']$/g, '');
        if (!fam) return;
        const key = `${fontSize}px|${fam}`;
        if (_loadedFontKeys.has(key)) return;
        const spec = `${fontSize}px "${fam}"`;
        try {
            // Fast path — if the browser already has it, don't wait at all.
            if (typeof document.fonts.check === 'function' && document.fonts.check(spec)) {
                _loadedFontKeys.add(key);
                return;
            }
            await document.fonts.load(spec);
            _loadedFontKeys.add(key);
        } catch (e) {
            // load() rejects only on really broken specs; cache the attempt anyway so we
            // don't loop on the same bad name every render.
            _loadedFontKeys.add(key);
            console.warn('[Visualist] document.fonts.load failed for', spec, e);
        }
    }

    // F5 / H61 — Visual.OnStartup / Visual.OnTrigger event-data context. Updated at the
    // top of every renderAll / handleRunTrigger so the trigger evaluators read the
    // payload that just arrived from Hub. EventData is stored both as the raw object
    // (for UserName / Message convenience accessors) and JSON-stringified (for the
    // EventData socket value).
    const triggerContext = {
        layerId:       null,
        triggerName:   '',
        eventData:     {},
        eventDataJson: '{}',
        timestamp:     0,
        // The timeline cursor in milliseconds. Every keyframe track (attrAnimated /
        // attrAnimatedColor) and every Time.Elapsed / Time.Oscillator / Time.Sawtooth node
        // samples against this, so it is what decides whether authored animation moves.
        //
        // TWO owners, and which one owns it FOR A GIVEN WIDGET is decided by
        // _productionClockOwnsWidgetTime():
        //   • design time — SCRUB pins it and PLAY advances it, from the Visualist WebView2
        //     bridge. Page-wide on the embedded per-widget preview (?widget=) and for the
        //     duration of a PLAY session; PER WIDGET on the whole-layer design preview
        //     (?client=editor), where the author scrubs ONE widget and the rest of the layer
        //     must keep running the production clock.
        //   • production (V5) — the global animator loop writes `now - activationStart` for the
        //     widget it is about to render, immediately before that render.
        //
        // It used to stay 0 forever on every production path, which meant every authored
        // keyframe and every Time.* node sampled at t=0 on stream: the author scrubbed, watched
        // it animate in the editor, saved, and got ONE static frame in OBS. Set to 0 at each
        // activation (handleRunTrigger, its idle revert, SET_ACTIVE_TRIGGER) so a trigger always
        // starts at the beginning of its timeline.
        timeMs:        0,
    };

    // Sweep 21 — keyframe sampling state. The Evaluator's attribute readers
    // consult `activeTimeline` to override static node attributes when a
    // matching `parameterPath` is present. Reset before every renderWidgetTrigger.
    let activeTimeline = null;

    // Track E — per-trigger master volume (0..1). Captured from trigger.Volume at
    // the top of renderWidgetTrigger and multiplied into every Audio.Play node's
    // own Volume in evalAudioPlay. Defaults to 1 so older layers (and any code path
    // that renders without a trigger.Volume) leave audio levels unchanged.
    let activeTriggerVolume = 1;

    // Audio one-shot guard — Audio.Play is a side-effecting SINK that gets re-visited
    // on EVERY renderWidgetTrigger pass. The per-widget animator, the design-time Play
    // loop, and timeline scrubbing all re-invoke renderWidgetTrigger many times a
    // second, so a node with Loop = false used to fire, finish, get torn down, then be
    // recreated + replayed on the very next render tick → it "loops even when
    // Loop = false". This generation distinguishes a GENUINE trigger activation
    // (RUN_TRIGGER, its idle revert, SET_ACTIVE_TRIGGER — each bumps it) from those
    // animator / play / scrub RE-renders (which deliberately do NOT bump it). A
    // one-shot plays exactly once per generation; same-generation re-renders skip the
    // replay. See ensureAudioElementAndPlay / evalAudioPlay / _audioPlayedGen.
    //
    // V7 widened what the generation owns: since Audio.Load's Path became wirable, the
    // resolved SOURCE is latched by the same generation. A path that resolves differently
    // between two renders of one activation is therefore ignored until the next genuine
    // activation — deliberately, because "src changed" arriving on the animator path would
    // replay the clip at frame rate.
    //
    // ★ PER WIDGET, not page-wide, and the map is the fix for a real multi-widget defect.
    //
    // A single page-wide counter made every widget's audio share one generation while the
    // animator slots that RE-render them are per widget. Two keyframed alert widgets, A and
    // B: firing B inside A's 2 s hold bumped the shared counter, so A's very next animator
    // frame saw `_audioPlayedGen.get(nodeA) !== gen`, read that as a fresh activation, and
    // REPLAYED A's one-shot — the "Loop = false audio loops" class, reached through a
    // neighbour instead of through a render tick. (Worse after V7: A's replay re-resolves its
    // wired Path against triggerContext, which B's activation had just overwritten, so A
    // replayed with B's CLIP.) Scoped per widget, B's activation is invisible to A's slot,
    // exactly as the per-widget activation CLOCK below is already scoped.
    //
    // Same shape as _widgetActivationStart: widgetId → counter, absent = 1 (never activated).
    // Absent-is-1 matters — _audioPlayedGen starts empty, so a first render of a never-bumped
    // widget still reads "new activation" and its onStartup audio fires once.
    const _audioActivationGen = new Map();   // widgetId → generation
    function bumpAudioActivation(widgetId) {
        if (!widgetId) return;
        _audioActivationGen.set(widgetId, audioActivationGen(widgetId) + 1);
    }
    function audioActivationGen(widgetId) {
        const g = _audioActivationGen.get(widgetId);
        return g === undefined ? 1 : g;
    }

    function _nowMs() {
        return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
    }

    // V5 — the per-widget ACTIVATION CLOCK, i.e. the thing that makes authored animation play
    // in OBS at all. A production render's triggerContext.timeMs is `now - activationStart` for
    // THAT widget, so every keyframe track and every Time.Elapsed / Time.Oscillator /
    // Time.Sawtooth node samples against the moment that widget last became something new —
    // not against a shared page clock, which would put two widgets triggered a minute apart at
    // wildly different points in their own timelines.
    //
    // Three of the four stamp sites are the ones that bump _audioActivationGen above —
    // handleRunTrigger, its idle revert, and handleSetActiveTrigger — because those are the
    // "genuine activation" moments. Every other render (an animator frame, a live patch, the
    // clock beat, a WIDGET_UPDATE drag) is a RE-render of the current activation and must not
    // restart the clock, or an alert would replay its intro on every repaint.
    //
    // The FOURTH site is renderWithTransition's post-dip render, and it is a RE-stamp of an
    // activation that has already happened rather than a new one: handleRunTrigger stamps, then
    // the dip-to-blank fade eats transitionMs (up to 1000 ms) of wall clock before the widget's
    // first live frame exists. Stamping again when the dip ENDS is what makes the track start
    // when the content actually appears; without it a sub-second intro was entirely consumed by
    // the fade. It deliberately does NOT bump the audio generation (see ★ below) — the audio for
    // this activation already fired on the pre-dip render.
    //
    // ★ The pairing is one-way ON PURPOSE: the clock stamp rides the activation sites, but the
    // clock NEVER bumps the audio generation. A tick that bumped it would re-fire every
    // one-shot Audio.Play sixty times a second — the 2026-06-23 "Loop = false audio loops"
    // bug, at frame rate. bumpAudioActivation(widgetId) still has exactly three callers, all
    // message-driven; nothing on the animator/clock route, and not the post-dip re-stamp,
    // can reach one. Both maps are keyed by WIDGET for the same reason: one widget's
    // activation must be invisible to another's re-render.
    const _widgetActivationStart = new Map(); // widgetId → _nowMs() at last activation
    function _stampWidgetActivation(widgetId) {
        if (!widgetId) return;
        _widgetActivationStart.set(widgetId, _nowMs());
        // A new activation restarts the clock, so a keyframe track that had played out (and let
        // the animator loop stop for this widget) must play again. Clearing the mark here rather
        // than at the three call sites keeps "activation" a single concept with a single effect.
        const slot = _widgetAnimators.get(widgetId);
        if (slot) slot.settledAtExtent = false;
    }

    /// Milliseconds since this widget's activation. The first read lazily stamps, so a widget
    /// that has only ever painted its onStartup idle state (no RUN_TRIGGER ever named it) still
    /// animates from t=0 instead of from page load — which for a 2 s keyframe track authored on
    /// onStartup is the difference between "plays" and "sits frozen on its last keyframe".
    function _widgetTimeMs(widgetId) {
        let start = _widgetActivationStart.get(widgetId);
        if (start === undefined) { start = _nowMs(); _widgetActivationStart.set(widgetId, start); }
        const t = _nowMs() - start;
        return t > 0 ? t : 0;
    }

    /// True when the PRODUCTION clock owns triggerContext.timeMs PAGE-WIDE.
    ///
    /// The two design-time transports own it instead: SCRUB pins a cursor and PLAY advances its
    /// own from performance.now(), and a production write landing between their frames would
    /// fight them for the canvas — the exact defect #6 fixed for GIF playback.
    ///
    /// Page-wide is the right scope for exactly two conditions:
    ///   • `?widget=` — the embedded per-widget preview renders ONE widget, and its whole reason
    ///     to exist is the editor's transport, so nothing on that page runs a production clock.
    ///   • a live PLAY session — the play tick writes timeMs on every frame for the widget it is
    ///     playing, so no other write can be trusted to survive between its frames either.
    ///
    /// ?client=editor — the whole-LAYER design preview — is deliberately NOT one of them: that is
    /// the surface the author lays a layer out on, and suppressing the clock there would hide the
    /// very animation this sprint exists to make play. But that page DOES scrub (see
    /// _designTimeClockOwners), so ownership on it is per widget, not per page.
    function _productionClockOwnsTime() {
        return widgetFilterId == null && _playState === null;
    }

    /// Widget ids whose triggerContext.timeMs is owned by a design-time transport — the
    /// per-WIDGET half of the ownership rule above, and the fix for a real defect on the
    /// whole-layer design preview.
    ///
    /// LayerPreviewPanel loads `/layer/<id>?client=editor`, so widgetFilterId is null there and
    /// _productionClockOwnsTime() is true — yet that panel has PostScrub / PostPlay and
    /// WidgetEditorView calls them (WidgetEditorView.xaml.cs: PostScrub on every playhead move,
    /// PostPlay on transport play). The result was that the author dragged the playhead, the
    /// scrubbed frame survived about one frame, and the animator loop overwrote timeMs with the
    /// production clock and repainted — the playhead simply did not work on the layer preview.
    ///
    /// Widening the page-wide gate to IS_DESIGN_TIME would have "fixed" it by stopping the layer
    /// preview from ever showing production animation, which is the opposite of what V5 is for.
    /// A per-widget latch keeps both: the scrubbed widget obeys the playhead, every other widget
    /// on the layer keeps animating.
    ///
    /// Set by handleScrub / handlePlay.
    ///
    /// ★ Stopping the transport does NOT clear it — stop HOLDS the frame it stopped on, which is
    /// what the author's gesture asked for and what handleStopPlay's animator-slot drop was
    /// already doing. Releasing it there (and clearing the whole set in the STOP_PLAY arm) was
    /// what snapped a just-played widget to its end pose.
    ///
    /// The pin is released by the signals that mean the playhead no longer describes what is on
    /// screen: RELEASE_TIME_CURSOR (the transport's STOP button — the explicit "done scrubbing"
    /// gesture, and the ONLY one of these reachable on the whole-layer preview), a RUN_TRIGGER
    /// for that widget (a production fire supersedes the editor's cursor — the widget is showing
    /// new content), a SET_ACTIVE_TRIGGER tab switch (which re-pins to 0 rather than deleting, so
    /// the new trigger's start frame holds), and softReloadLayer (a save is the author's start
    /// over).
    ///
    /// ★ A MAP, widgetId → pinned timeMs, and carrying the value is the whole point. Recording
    /// only WHO owns the cursor left every production write site with nothing to write, so each
    /// one degraded to "skip the write" — and triggerContext.timeMs is a single page global that
    /// the animator loop rewrites for OTHER widgets between those renders. A non-scrub render of a
    /// design-time-owned widget (a LIVE_PATCH, a WIDGET_UPDATE drag, a resolution-change
    /// renderAll) therefore painted it at a FOREIGN widget's clock — the exact hazard the
    /// surrounding comments exist to prevent. With the cursor stored, every such render paints the
    /// value the editor actually pinned. Read it through _applyWidgetTimeCursor, never directly.
    const _designTimeClockOwners = new Map();

    /// True when the production activation clock owns triggerContext.timeMs for THIS widget.
    /// Every production timeMs write goes through this (renderAll, patchWidgetUpdate, the
    /// consumer pass, the animator loop, renderWithTransition's post-dip render).
    function _productionClockOwnsWidgetTime(widgetId) {
        return _productionClockOwnsTime() && !_designTimeClockOwners.has(widgetId);
    }

    /// Establishes triggerContext.timeMs for a render of `widgetId` on a PRODUCTION path (i.e.
    /// anything that is not the SCRUB / PLAY / SET_ACTIVE_TRIGGER transport itself). The single
    /// place the two-owner rule is applied, so the five such sites cannot drift:
    ///
    ///   • production owns this widget's cursor  ⇒ its own activation clock,
    ///   • a design-time transport owns it       ⇒ the value that transport PINNED,
    ///   • neither has a value to offer          ⇒ leave the singleton alone.
    ///
    /// The third case is not a gap. It is the `?widget=` preview before its first SCRUB and the
    /// non-played widgets during a PLAY session: _productionClockOwnsTime() is false page-wide
    /// there, and the transport writes timeMs directly for the widget it drives. Writing anything
    /// of our own for a widget nobody has pinned would be a guess; leaving it is the pre-V5
    /// behaviour and provably no worse.
    function _applyWidgetTimeCursor(widgetId) {
        if (_productionClockOwnsWidgetTime(widgetId)) {
            triggerContext.timeMs = _widgetTimeMs(widgetId);
            return;
        }
        const pinned = _designTimeClockOwners.get(widgetId);
        if (pinned !== undefined) triggerContext.timeMs = pinned;
    }

    // Device-pixel scale of the main canvas backing store (= dpr from
    // applyResolution). The dip-to-blank transition snapshots a widget region from
    // the canvas in DEVICE pixels, so it must read this rather than assume 1:1.
    let deviceScale = 1;

    // Translation request/response correlation. Browser-side cache keyed by
    // JSON.stringify([text, lang]) so a `|` inside text can't collide with the
    // separator (M63 — previous `${text}|${lang}` was ambiguous when text contained '|').
    // Cache is bounded; oldest entries evicted on overflow.
    const TRANSLATE_CACHE_MAX = 256;
    const translateCache  = new Map();   // insertion order = age, so first key is oldest
    const pendingTranslate = new Map();  // reqId → resolve fn
    let nextReqId = 1;

    function translateCacheKey(text, lang) {
        return JSON.stringify([text, lang]);
    }
    function translateCacheGet(key) {
        if (!translateCache.has(key)) return undefined;
        // Touch on hit → reinsert so it becomes most-recent.
        const v = translateCache.get(key);
        translateCache.delete(key);
        translateCache.set(key, v);
        return v;
    }
    function translateCacheSet(key, value) {
        if (translateCache.has(key)) translateCache.delete(key);
        translateCache.set(key, value);
        if (translateCache.size > TRANSLATE_CACHE_MAX) {
            // Evict oldest until back under cap.
            const overflow = translateCache.size - TRANSLATE_CACHE_MAX;
            const it = translateCache.keys();
            for (let i = 0; i < overflow; i++) {
                const k = it.next().value;
                if (k === undefined) break;
                translateCache.delete(k);
            }
        }
    }

    // M46 / L48 — per-trigger metadata cached on first visit. Avoids re-scanning the
    // graph on every render and gives us a single place to enforce graph-shape rules.
    //   • displayNode    — the resolved Display sink (with L48 dedupe + warn applied).
    //   • consumesClock  — true when the graph carries a Clock.Now node. The LAST surviving
    //     consumption flag: M46's four data flags (caption / timer / loyalty / counter) were
    //     title-prefix approximations of "does this widget read live data", and the Overlay
    //     Live Channel answers that question exactly instead — per KEY, via _widgetLiveKeys,
    //     so a patch touching timer.main.* re-renders only the widgets bound to timer.main.*
    //     rather than every widget carrying any timer node. Clock.Now is the one reader with
    //     no Hub producer at all (it reads the OBS machine's own Date.now()), so it is NOT a
    //     channel consumer and keeps its own flag and its own 1 Hz heartbeat — which V5 folded
    //     into the single global animator tick (it is no longer a setInterval of its own).
    //   • consumesTime   — true when rendering this trigger at an ADVANCING timeMs produces a
    //     different picture: the trigger has keyframes, or the graph carries a Time.* node that
    //     reads the clock. This is the flag that opts the widget into the animator loop; see
    //     TIME_CONSUMING_TITLES below for why the set has three entries and not four.
    //   • timeExtentMs   — how long that stays true. null = forever (a Time.* node); otherwise
    //     the last keyframe's time, past which every sample repeats and the loop can stop.
    // Keyed by `${widgetId}|${triggerName}`. Cleared on layer load.
    const _triggerMeta = new Map();

    // The graph nodes whose VALUE depends on triggerContext.timeMs. Membership here is what
    // promotes a widget into the animator loop (via consumesTime → requestWidgetAnimator), so
    // an omission means "authored animation silently renders as a static frame in OBS".
    //
    // ★ Time.Easing is deliberately ABSENT. evalTimeEasing reads only its own `T` socket and
    // never touches timeMs — it is a curve, not a clock. When its T is driven by one of the
    // three below, THAT node's presence has already set the flag, so adding Easing here would
    // only promote graphs whose easing input is a static constant: a widget re-rendered at
    // frame rate to produce a byte-identical picture forever. Do not "fix" this omission.
    const TIME_CONSUMING_TITLES = new Set(['Time.Elapsed', 'Time.Oscillator', 'Time.Sawtooth']);
    function _triggerMetaKey(widgetId, triggerName) { return `${widgetId}|${triggerName}`; }
    function getTriggerMeta(widget, trigger) {
        const key = _triggerMetaKey(widget.id, trigger.name);
        let meta = _triggerMeta.get(key);
        if (meta) return meta;

        const nodes = (trigger.graph && trigger.graph.Nodes) || [];

        // L48 — Display dedupe. The "only one Display sink per trigger" rule was previously
        // assumed to hold via the editor; if a hot-reloaded .phxlayer has more than one
        // (paste, migration, manual JSON edit), warn and pick the FIRST in node order so
        // behavior matches the old baseline (`.find(...)`).
        const displays = nodes.filter(n => n.Title === 'Display');
        if (displays.length > 1) {
            console.warn(
                `[Visualist] Trigger "${trigger.name}" on widget "${widget.id}" has ` +
                `${displays.length} Display sinks; only the first will render. ` +
                'Remove the duplicates in Visualist to silence this warning.');
        }
        const displayNode = displays[0] || null;

        // Clock consumption flag — Clock.Now is browser-autonomous (it reads the OBS machine's
        // own wall clock, and no Hub producer exists for it), so it cannot ride the Overlay
        // Live Channel's key-narrowed re-render pass: it has no key. A dedicated 1 Hz
        // heartbeat re-renders any widget carrying one, which is why this is the one
        // consumption flag the channel did not subsume.
        const consumesClock = nodes.some(n => n.Title === 'Clock.Now');

        // Time consumption flag — the widget-animator opt-in for authored animation.
        //
        // Two independent sources, either of which makes an advancing timeMs visible:
        //   • the trigger's timeline carries at least one keyframe (attrAnimated /
        //     attrAnimatedColor sample it at triggerContext.timeMs), or
        //   • the graph carries a node that reads the clock directly (TIME_CONSUMING_TITLES).
        // `keyframes` is the serialized WidgetTimeline field name — the same one attrAnimated
        // reads, so the two halves cannot disagree about what "has keyframes" means.
        const timelineKeyframes = (trigger.timeline && trigger.timeline.keyframes) || null;
        const hasTimeNode  = nodes.some(n => TIME_CONSUMING_TITLES.has(n.Title));
        const hasKeyframes = Array.isArray(timelineKeyframes) && timelineKeyframes.length > 0;
        const consumesTime = hasKeyframes || hasTimeNode;

        // How long an advancing timeMs can still CHANGE this trigger's picture.
        //   null  ⇒ unbounded: a Time.* node samples the clock for as long as the page lives.
        //   >= 0  ⇒ the last keyframe's time. keyframeSampleScalar clamps to the final keyframe
        //           for anything at or past it, so every sample beyond this is byte-identical.
        //
        // This is what lets the animator loop STOP. Without it, a widget carrying a 2 s intro
        // track would keep re-evaluating its whole graph at display refresh rate for the rest of
        // the stream to redraw the same pixels — a real cost on a streaming PC with a dozen
        // browser sources, and a brand-new one, because before V5 such a widget was demoted
        // after its first paint (which is exactly the bug: it never animated at all).
        //
        // Read from `time`, the serialized Keyframe field, and finite-guarded: a malformed
        // .phxlayer can carry NaN, and `k.time || 0` (what keyframeSampleScalar uses) treats
        // NaN as 0, so ignoring non-finite times here agrees with how they actually sample.
        let timeExtentMs = 0;
        if (hasTimeNode) {
            timeExtentMs = null;
        } else if (hasKeyframes) {
            for (const k of timelineKeyframes) {
                const t = Number(k && k.time);
                if (Number.isFinite(t) && t > timeExtentMs) timeExtentMs = t;
            }
        }

        // Sink-category scans memoized here too (graph is immutable until reload) so
        // renderWidgetTrigger doesn't re-filter/-find the full node list every frame.
        const audioSinks = nodes.filter(n => n.Title === 'Audio.Play');
        const overlaySinks = nodes.filter(n => n.Title === 'WebOverlay.Custom');
        // V15 — the second DOM-track sink. Memoized in the same object for the same reason:
        // the graph is immutable until reload, and renderWidgetTrigger must not re-filter
        // the node list on every animator frame.
        const playerSinks = nodes.filter(n => n.Title === 'Player.Embed');
        const completeSink = nodes.find(n => n.Title === 'Visual.Complete') || null;

        meta = {
            displayNode, consumesClock, consumesTime, timeExtentMs,
            audioSinks, overlaySinks, playerSinks, completeSink,
        };
        _triggerMeta.set(key, meta);
        return meta;
    }

    /// Returns true when ANY of the widget's triggers references a Clock.Now node — the
    /// selector the 1 Hz clock heartbeat narrows its re-render pass with.
    ///
    /// The four siblings this used to sit beside (widgetConsumesCaption / Timer / Loyalty /
    /// Counter) are gone: each existed to answer "does this widget read live data of family X"
    /// for one bespoke push frame, and _widgetLiveKeys now answers the sharper question "which
    /// live KEYS does this widget read" for all families at once. Clock.Now has no key because
    /// it has no Hub producer, so it keeps a selector of its own.
    function widgetConsumesClock(widget) {
        if (!widget || !widget.triggers) return false;
        for (const trig of widget.triggers) {
            // Skip a null/nameless ELEMENT, not just a null list — see widgetConsumesTime for
            // why `triggers: [null]` genuinely reaches the browser.
            if (!trig || !trig.name) continue;
            if (getTriggerMeta(widget, trig).consumesClock) return true;
        }
        return false;
    }

    /// Returns true when ANY of the widget's triggers is time-consuming (keyframes or a Time.*
    /// node). Deliberately shaped as a widget-level rollup like widgetConsumesClock — a widget
    /// is either in the animator loop or it is not, and the loop renders whichever trigger the
    /// slot was promoted with rather than re-deciding per trigger every frame.
    ///
    /// NOT the steady-state promotion path: promotion/demotion goes through
    /// requestWidgetTimeAnimator() from renderWidgetTrigger so a widget that STOPS being
    /// keyframed is demoted by the same latch that promoted it.
    ///
    /// This rollup answers the whole-layer question — "does this layer need the loop at all,
    /// BEFORE anything has rendered" — and it has exactly one caller: _refreshAnimatorDemand,
    /// which SEEDS a slot for every time-consuming widget the moment `layer` is known. Without
    /// that seed the loop's start depended on renderAll's promotions having already landed, and
    /// renderAll is not awaited at bootstrap: on a layer that also carries a Clock.Now widget the
    /// loop had already armed its 1 Hz clock TIMEOUT, so the first animated frame waited up to a
    /// full second behind it (and a track shorter than that delay rendered exactly one frame, at
    /// its final keyframe pose). Seeding + the timeout preemption in _ensureAnimatorLoop are the
    /// two halves of that fix.
    ///
    /// ★ The element guard is the load-bearing part of that seed, not paranoia. `triggers: [null]`
    /// in a hand-edited / corrupted .phxlayer deserialises to a real HOLE, and
    /// LayerSerializer.Deserialize HEALS AROUND such an element (`if (trigger is null) continue;`)
    /// rather than removing it — so /api/layer/<id> genuinely serves one to the browser. Since
    /// _refreshAnimatorDemand walks EVERY widget's trigger list unconditionally (it seeds), a
    /// `trig.name` throw in here is not one dark widget: it aborts the demand refresh, and at
    /// bootstrap that used to abort the whole `.then` chain, so one malformed element took the
    /// layer's WebSocket with it (see the bootstrap's connect-before-seed ordering). Same skip
    /// LayerRuntime.FindTrigger uses on the Hub side, for the same input.
    function widgetConsumesTime(widget) {
        if (!widget || !widget.triggers) return false;
        for (const trig of widget.triggers) {
            if (!trig || !trig.name) continue;
            if (getTriggerMeta(widget, trig).consumesTime) return true;
        }
        return false;
    }

    // ── Overlay Live Channel — subscription derivation ───────────────────────
    //
    // Client mirror of OverlayLiveStore.MaxSubscriptionKeys / MaxSubscriptionPrefixes. Hub
    // truncates an over-cap subscription and logs once per layer; capping on this side too
    // keeps both halves telling the same story instead of the browser believing it is
    // subscribed to keys Hub silently dropped.
    const LIVE_MAX_KEYS     = 512;
    const LIVE_MAX_PREFIXES = 64;

    // Shared empty result so the buildLiveSubscription walk allocates nothing for the
    // (overwhelmingly common) node that reads no live keys at all.
    const _NO_LIVE_KEYS = Object.freeze([]);

    // Fixed key sets, frozen and shared so the per-node walk allocates nothing for the readers
    // whose keys don't depend on an attribute.
    //
    // Loyalty.Leaderboard takes the currency label as well as the board, because its Format
    // template carries a {currency} token. Loyalty.Balance takes the board ONLY, exactly as the
    // reader matrix specifies: it derives one viewer's balance out of the board, and a
    // {currency} token in ITS format is substituted empty rather than read from a key this
    // widget never subscribed — a value whose freshness we could not vouch for is worse than a
    // blank.
    const _LIVE_KEYS_LOYALTY_BOARD   = Object.freeze([LIVE_KEY_LOYALTY_BOARD, LIVE_KEY_LOYALTY_CURRENCY]);
    const _LIVE_KEYS_LOYALTY_BALANCE = Object.freeze([LIVE_KEY_LOYALTY_BOARD]);
    // caption.source_language / caption.target_language are deliberately absent: no socket
    // reads them, and Hub withholds a key with no reader rather than publishing dead weight.
    const _LIVE_KEYS_CAPTION         = Object.freeze([LIVE_KEY_CAPTION_ORIGINAL, LIVE_KEY_CAPTION_TRANSLATED]);
    // V15 — the songrequest.* family as ONE prefix. The root must stay byte-identical to
    // the one SongRequestService.PublishOverlay writes under and to
    // PlayerEmbedSinkNode.SongRequestKeyPrefix; three spellings of one root is a blank
    // player with a running queue and no error on either side.
    const _LIVE_KEYS_SONGREQUEST     = Object.freeze(['songrequest.*']);

    // widgetId → string[] of live keys/prefixes that widget's graphs read. Rebuilt as a
    // byproduct of buildLiveSubscription, whose only callers are the two moments the graph
    // set can have changed (socket open, end of softReloadLayer) — so unlike _triggerMeta it
    // needs no invalidation hook of its own. Holds an entry for EVERY key-reading widget on
    // the layer, visible or not; the render pass narrows to the visible one. Two readers:
    // renderLiveConsumers uses `size === 0` as the inert-layer gate, and the pass uses each
    // widget's list to decide whether it cares about the keys a given patch carried.
    const _widgetLiveKeys = new Map();

    /// Returns the Overlay Live Channel keys — literal `a.b.c` keys, or `a.b.*` prefixes —
    /// that ONE graph node reads. The union over the layer's graphs is what LIVE_HELLO
    /// subscribes to, so a node absent from this dispatcher never receives live data.
    ///
    /// Dispatch is on node.Title, matching how getTriggerMeta and the Evaluator identify
    /// nodes. A node title absent from this switch never receives live data — so every arm
    /// here is paired with a reader in the Evaluator below, and the key each arm returns is
    /// derived by the SAME helper the reader looks up with (liveTimerRoot / liveCounterKey /
    /// liveVarKey, or one of the frozen fixed sets). That pairing is the whole point: a
    /// subscription and a lookup that normalise differently produce a permanently blank widget
    /// with a running producer, a valid graph and no error on either side.
    ///
    /// A reader whose key depends on an attribute the author left empty returns nothing rather
    /// than a partial key — an unnamed Counter.Value is a graph the author has not finished,
    /// not a subscription to `counter..count`.
    function liveKeysForNode(node) {
        const title = (node && typeof node.Title === 'string') ? node.Title : '';
        if (!title) return _NO_LIVE_KEYS;
        switch (title) {
            // The author-facing binding node: ONE literal key, whoever published it. A tool
            // key and an overlay.publish key are indistinguishable here by design.
            case 'Var.Live': {
                const key = liveVarKey(node);
                return key ? [key] : _NO_LIVE_KEYS;
            }

            // The three timer readers share one root and one 13-field family, so they
            // subscribe the whole family as ONE prefix rather than 13 exact keys — which also
            // means adding a field Hub-side needs no client change. See liveTimerRoot for why
            // an empty TimerName resolves to the fixed `timer.__default.*` mirror.
            case 'Timer.Remaining':
            case 'Countdown.Remaining':
            case 'Stopwatch.Elapsed':
                return [liveTimerRoot(node) + '*'];

            case 'Counter.Value': {
                const key = liveCounterKey(node);
                return key ? [key] : _NO_LIVE_KEYS;
            }

            case 'Loyalty.Leaderboard': return _LIVE_KEYS_LOYALTY_BOARD;
            case 'Loyalty.Balance':     return _LIVE_KEYS_LOYALTY_BALANCE;
            case 'Caption.LiveCaption': return _LIVE_KEYS_CAPTION;

            // V10 — the goal family. FOUR fields under one root, subscribed as ONE prefix for
            // the same reason the timer trio does it: a field added Hub-side then needs no
            // client change, and the reader cannot ask for a key the subscription omitted.
            // An empty Kind returns nothing rather than a partial root — see liveGoalRoot for
            // why a bare 'current' / 'target' subscription would bind a stranger's key.
            case 'Goal.Progress': {
                const root = liveGoalRoot(node);
                return root ? [root + '*'] : _NO_LIVE_KEYS;
            }

            // V10 — the array reader. One literal key, like Var.Live; an unnamed node is a
            // graph the author has not finished, not a subscription to ''.
            case 'List.Live': {
                const key = liveListKey(node);
                return key ? [key] : _NO_LIVE_KEYS;
            }

            // V15 — the iframe player, queue-fed half. ONE prefix over the whole
            // songrequest.* family, for the same reason the timer trio and Goal.Progress
            // take one: it reads five of the eleven keys today (state / video_id /
            // play_token / volume, and the id is what everything else hangs off), and a
            // field added Hub-side then needs no client change.
            //
            // The CLIP source subscribes nothing — its value arrives on the Clip input over
            // the trigger channel, not off the live channel — and returning the prefix
            // anyway would make every shoutout widget on the layer widen the layer-wide
            // hello for data it never reads.
            case 'Player.Embed': {
                const src = stripQuotes(String(attr(node, 'Source', 'songrequest') || 'songrequest'))
                    .trim().toLowerCase();
                return src === 'clip' ? _NO_LIVE_KEYS : _LIVE_KEYS_SONGREQUEST;
            }

            default: return _NO_LIVE_KEYS;
        }
    }

    /// Walks every widget/trigger graph and returns the deduped, sorted union of the live
    /// keys this layer reads — the payload of LIVE_HELLO. Refreshes _widgetLiveKeys on the
    /// way through, since it is the same walk.
    function buildLiveSubscription() {
        _widgetLiveKeys.clear();
        if (!layer || !Array.isArray(layer.widgets)) return [];

        const exact    = new Set();
        const prefixes = new Set();
        for (const widget of layer.widgets) {
            // The announced set is deliberately LAYER-WIDE — it must NOT be narrowed by
            // isWidgetVisible / widgetFilterId. Hub keys subscriptions per LAYER, and
            // Visualist's `?widget=` preview page connects to the SAME /hud/<layer> socket as
            // the live OBS source. A narrowed hello therefore makes HandleHelloAsync replace
            // the whole layer's subscription with one widget's keys, silently freezing every
            // other widget on the live overlay with no recovery short of an OBS cache refresh.
            // Every socket on a layer shows the same .phxlayer, so the union of all sockets'
            // needs IS the layer's key set and per-layer storage is correct by construction.
            // A preview socket then receives a few patches it does not repaint — strictly
            // cheaper than freezing the live overlay. Per-widget narrowing belongs in
            // _widgetLiveKeys below, i.e. at render time, the only place it changes anything.
            const own = new Set();
            for (const trig of (widget.triggers || [])) {
                if (!trig) continue;   // `triggers: [null]` — see widgetConsumesTime
                const nodes = (trig.graph && trig.graph.Nodes) || [];
                for (const node of nodes) {
                    for (const key of liveKeysForNode(node)) {
                        if (typeof key !== 'string' || !key) continue;
                        own.add(key);
                        (key.endsWith('.*') ? prefixes : exact).add(key);
                    }
                }
            }
            // _widgetLiveKeys deliberately keeps a widget's full key list even when the cap
            // below drops some of them from the announced set: a key Hub never sends simply
            // never matches a patch, whereas pruning here would risk skipping a widget that
            // DOES read a key that survived.
            if (own.size > 0) _widgetLiveKeys.set(widget.id, Array.from(own));
        }

        // Sort each class BEFORE truncating so an over-cap layer keeps a deterministic
        // subset — a reconnect has to re-announce the same keys, not a fresh sample of
        // Set-iteration order.
        const kept = Array.from(prefixes).sort().slice(0, LIVE_MAX_PREFIXES)
            .concat(Array.from(exact).sort().slice(0, LIVE_MAX_KEYS));
        return kept.sort();
    }

    /// True when a subscription entry (exact key or `<root>.*` prefix) covers any key in
    /// `changedKeys`. Mirrors OverlayLiveStore's match rule exactly — the prefix compare
    /// drops only the `*`, so "timer.main.*" tests against "timer.main." — so both halves
    /// agree on what a prefix subscription covers.
    function _liveEntryMatchesChanged(entry, changedKeys) {
        if (entry.endsWith('.*')) {
            const prefix = entry.slice(0, -1);
            for (const k of changedKeys) if (k.startsWith(prefix)) return true;
            return false;
        }
        return changedKeys.has(entry);
    }

    // H62 — per-widget render mutex. Without this, a consumer pass (a live-channel patch or
    // the clock heartbeat) could overlap an in-flight RUN_TRIGGER render of the same widget,
    // causing the canvas to flicker between the two intermediate states.
    const widgetRenderLocks = new Map(); // widgetId → Promise (current render)
    async function withWidgetLock(widgetId, fn) {
        const previous = widgetRenderLocks.get(widgetId);
        let release;
        const next = new Promise(r => { release = r; });
        widgetRenderLocks.set(widgetId, next);
        if (previous) { try { await previous; } catch { /* prior render errored — keep going */ } }
        try { return await fn(); }
        finally {
            release();
            // Only clear our entry if no newer queue overlapped us.
            if (widgetRenderLocks.get(widgetId) === next) widgetRenderLocks.delete(widgetId);
        }
    }

    function setStatus(msg) { statusEl.textContent = `[${layerId}] ${msg}`; }

    // ── Bootstrap ────────────────────────────────────────────────────────────

    fetch(`/api/layer/${encodeURIComponent(layerId)}`)
        .then(r => {
            if (!r.ok) throw new Error(`layer fetch failed: HTTP ${r.status}`);
            return r.json();
        })
        .then(data => {
            layer = data;
            applyResolution();
            renderAll(); // initial paint of every widget's onStartup trigger
            // ★ ORDER MATTERS: the socket is opened BEFORE the animator is armed.
            //
            // Everything below this line is best-effort local work; the WebSocket is the layer's
            // only link to Hub — it is what registers the layer as ACTIVE (LayerRegistry presence),
            // what delivers RUN_TRIGGER, and what carries the Overlay Live Channel. It used to be
            // opened LAST, so any throw while deriving animator demand landed in the `.catch`
            // below and the socket was never opened at all: the overlay went permanently dark and
            // Hub's inactive-layer short-circuit silently fast-succeeded every wait_for_visual.
            // One malformed trigger element was enough (see widgetConsumesTime). Connect first and
            // that entire class of bootstrap fault costs at most the animation, never the link.
            connectSocket();
            // Arm the global animator loop for this layer. renderAll's own renders promote any
            // time-consuming / animated-media widget through requestWidgetAnimator, but a
            // Clock.Now-only layer has nothing to promote — the loop's other reason to run is
            // the 1 Hz beat, and this is what tells it that reason exists. Safe to run after
            // connectSocket(): inbound frames cannot be dispatched until this synchronous block
            // returns, so no message handler can observe a half-armed animator.
            _refreshAnimatorDemand();
        })
        .catch(err => {
            setStatus(`error: ${err.message}`);
            console.error(err);
        });

    function applyResolution() {
        // Single-widget preview mode (Visualist): the canvas IS the widget.
        // Sized to the widget's rect so the editor pane scales the widget's
        // pixels uniformly, no letterbox.
        let cw = layer.resolution.width;
        let ch = layer.resolution.height;
        if (widgetFilterId && Array.isArray(layer.widgets)) {
            const w = layer.widgets.find(x => x.id === widgetFilterId);
            if (w && w.rect && w.rect.width > 0 && w.rect.height > 0) {
                cw = w.rect.width;
                ch = w.rect.height;
            }
        }
        // Device-pixel-ratio aware sizing (Majo's blur fix, global half). The
        // BACKING STORE is logical×dpr so HiDPI surfaces (the Visualist preview
        // pane especially) get a 1:1 device-pixel canvas instead of a logical
        // canvas the GPU then upscales (which softens everything, text most of
        // all). ctx is pre-scaled by dpr so ALL existing draw code keeps using
        // logical coordinates unchanged. logicalW/logicalH track the logical
        // size for the handful of call sites that read the canvas dims AS a
        // coordinate. dpr === 1 (OBS native, 100%-scale monitors) ⇒ identical
        // to the previous behaviour, byte for byte.
        const dpr = Math.max(1, window.devicePixelRatio || 1);
        deviceScale = dpr;
        logicalW = cw;
        logicalH = ch;
        canvas.width  = Math.round(cw * dpr);
        canvas.height = Math.round(ch * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        // Letterbox: scale canvas via CSS to fit viewport while preserving aspect
        // ratio. Computed from the LOGICAL size (not the device backing store).
        const scale = Math.min(window.innerWidth / cw, window.innerHeight / ch);
        canvas.style.width  = `${cw * scale}px`;
        canvas.style.height = `${ch * scale}px`;
        // The canvas just changed size on screen — drop the measured rect so the
        // next reader re-measures against the new letterbox.
        invalidateCanvasRect();
    }
    // Named so we can detach on pagehide — without that the closure pins all
    // captured state for as long as the BFCache holds the page, even after
    // navigation away. Production OBS browser sources reload on layer change,
    // so the cleanup mostly matters for the WebView2 preview.
    //
    // QC28-07 — 100ms trailing-edge debounce. Visualist's drag-resize used to
    // storm the canvas-realloc path inside applyResolution (which does a
    // canvas.width / .height assignment — full bitmap clear + GPU upload per
    // call). Settling on the final dimensions once the user stops dragging
    // is plenty for the human eye and avoids a several-hundred-Hz realloc
    // burst.
    let _resizeDebounceTimer = null;
    function _onWindowResize() {
        // Invalidate immediately, NOT inside the debounced body: the canvas's
        // on-screen rect is already wrong the moment the viewport changes, and the
        // overlay track re-reads it every render during the 100ms settle window.
        invalidateCanvasRect();
        if (_resizeDebounceTimer !== null) {
            try { clearTimeout(_resizeDebounceTimer); } catch { /* ignore */ }
        }
        _resizeDebounceTimer = setTimeout(() => {
            _resizeDebounceTimer = null;
            applyResolution();
            // Manipulator overlay layout depends on the canvas's screen rect, so
            // re-sync it after the layer canvas has been rescaled to the new
            // viewport. Cheap when no manipulator is active (early-return inside).
            syncManipulatorOverlaySize();
            drawManipulator();
            // DOM overlays track the letterboxed canvas rect too — reposition every
            // mounted WebOverlay.Custom host after the canvas rescales.
            syncWebOverlayLayout();
            // V15 — and every mounted iframe player, which rides the same track and the
            // same logical-px + transform-scale placement.
            syncPlayerEmbedLayout();
        }, 100);
    }
    window.addEventListener('resize', _onWindowResize);

    // Bug #5 — undo/redo forwarding. After a manipulator drag the embedded-preview
    // WebView2 holds keyboard focus, so Ctrl+Z is swallowed by the browser and never
    // reaches the graph canvas's document hotkey → "Ctrl+Z doesn't undo image
    // settings". Forward the chord to the WinUI host (REQUEST_UNDO / REQUEST_REDO);
    // WidgetSinglePreviewPanel routes it to the pillar MainView's Undo()/Redo().
    // Keyed on evt.key (the LAYOUT-MAPPED keytop label), NOT evt.code (physical
    // position). On a German QWERTZ keyboard the physical Z position is the "Y"
    // keytop, so evt.code === 'KeyZ' fired on the wrong key and Ctrl+Z never undid
    // (Majo's "doesn't accept all keyboard variations"). evt.key === 'z' is
    // whatever key is labelled Z on the active layout — matches the WinUI side's
    // VirtualKey.Z. Guarded to WebView2 hosts (chrome.webview present) so the OBS
    // browser source — no chrome.webview, keys mean nothing there — is left alone.
    function onGlobalUndoRedoKey(evt) {
        if (typeof chrome === 'undefined' || !chrome.webview || !chrome.webview.postMessage) return;
        if (!(evt.ctrlKey || evt.metaKey)) return;
        const k = (evt.key || '').toLowerCase();
        let type = null;
        if (k === 'z')      type = evt.shiftKey ? 'REQUEST_REDO' : 'REQUEST_UNDO';
        else if (k === 'y') type = 'REQUEST_REDO';
        if (!type) return;
        try { chrome.webview.postMessage({ type }); } catch { /* host went away */ }
        evt.preventDefault();
    }
    window.addEventListener('keydown', onGlobalUndoRedoKey);

    // Page-lifetime cleanup. Detaches the global listeners we own and tears
    // down the video / audio pools so DOM nodes don't linger across BFCache
    // restores. Called on pagehide (more reliable than beforeunload across
    // browsers, especially Chromium's BFCache path used inside OBS / WebView2).
    function _disposeGlobals() {
        try { window.removeEventListener('resize',  _onWindowResize); } catch { }
        try { window.removeEventListener('mouseup', onManipMouseUp); } catch { }
        if (manipulatorOverlay) {
            try { manipulatorOverlay.removeEventListener('mousedown', onManipMouseDown); } catch { }
            try { manipulatorOverlay.removeEventListener('mousemove', onManipMouseMove); } catch { }
        }
        for (const [, v] of _videoPool) _teardownVideoElement(v);
        _videoPool.clear();
        for (const [, a] of _audioPool) _teardownAudioElement(a);
        _audioPool.clear();
        _audioPlayedGen.clear();
        _audioActivationGen.clear();
        // The rejected-media-path report latch is page state like everything above: a BFCache
        // restore that kept it would silently swallow the FIRST report on the restored page.
        _reportedRejectedMediaPaths.clear();
        // Same argument for the missing-arg latch, which became page-scoped for the frame-rate
        // reason recorded on _reportedMissingArgs and therefore acquired the same dispose duty.
        _reportedMissingArgs.clear();
        // Same argument, same class of latch — and it matters more now that this one gates a frame
        // to Hub rather than only a console line: a restored page whose latch survived would report
        // a malformed List.Live key nowhere at all.
        //
        // ★ THE MEMO HAS TO GO WITH IT, or the clear above does not achieve that. _listStringMemo
        // short-circuits the JSON-string arm BEFORE the parse (`v === _listStringMemo.src` returns
        // the cached rows), so a restored page whose memo still holds the malformed string never
        // reaches the report at all, cleared latch or not — and on the single-List.Live-node layer
        // where this is likeliest, that one string IS the one-slot memo's occupant. Note what the
        // stale memo can and cannot do: it can NOT serve a wrong value across a dispose (JSON.parse
        // is pure, so the cached rows remain the right answer for that exact string), it can only
        // swallow the diagnostic — which is precisely what the line above exists to restore.
        _listParseWarned.clear();
        _listStringMemo.src  = null;
        _listStringMemo.rows = _NO_LIST_ROWS;
        _stopAnimatorLoop();
        _widgetAnimators.clear();
        _animatorInFlight.clear();
        _animatorResumeId = null;
        _widgetActivationStart.clear();
        // Design-time cursor ownership dies with the page too: a BFCache restore that kept it
        // would leave widgets pinned to a playhead the editor no longer has open, i.e. frozen.
        _designTimeClockOwners.clear();
        imageCache.clear();
    }
    window.addEventListener('pagehide', _disposeGlobals);

    // ── Preview helpers (widget-filter + bg backdrop) ────────────────────────

    /// Returns the rect compositor.js renders this widget INTO. Layer mode →
    /// the widget's authored rect (compositor places multiple widgets across
    /// the layer canvas). Widget-filter mode → the full canvas, so the single
    /// widget fills the preview pane.
    function widgetRenderRect(widget) {
        if (widgetFilterId) {
            return { x: 0, y: 0, width: logicalW, height: logicalH };
        }
        return widget.rect;
    }

    /// Rect intersection / union helpers for the targeted WIDGET_UPDATE repaint
    /// (patchWidgetUpdate). Rects are {x,y,width,height} in logical layer pixels.
    function rectsIntersect(a, b) {
        if (!a || !b) return false;
        return a.x < b.x + b.width  && a.x + a.width  > b.x
            && a.y < b.y + b.height && a.y + a.height > b.y;
    }
    function unionRect(a, b) {
        if (!a) return b;
        if (!b) return a;
        const x1 = Math.min(a.x, b.x);
        const y1 = Math.min(a.y, b.y);
        const x2 = Math.max(a.x + a.width,  b.x + b.width);
        const y2 = Math.max(a.y + a.height, b.y + b.height);
        return { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
    }

    /// Image-loader auto-fit (Majo). A freshly-loaded source is scaled to FIT
    /// inside the widget rect — contain: preserve aspect, touch the tight axis,
    /// letterbox the other — scaling small art UP and oversized art DOWN so a bare
    /// Image.Load fills its widget by default instead of sitting at native size in
    /// the middle. Returns the LOGICAL width/height the Display sink draws the
    /// (retained, native-resolution) bitmap into, so the 4-arg drawImage down/up-
    /// samples a crisp result. faa466a9's model is preserved: the widget still owns
    /// size/aspect (this only fits a raw load to it), Display still draws 1:1 +
    /// centred + clips, and a downstream Image.Transform can still push content past
    /// the widget edge to be clipped. Re-fits every render because `frame` (the
    /// widget rect) is read fresh per renderWidgetTrigger, so resizing the widget
    /// rescales the image automatically. No frame / degenerate dims → native size
    /// unchanged (legacy / tooling Evaluators built without a frame).
    function fitLoadedImageToFrame(iw, ih, frame) {
        if (!frame || !(iw > 0) || !(ih > 0) || !(frame.width > 0) || !(frame.height > 0))
            return { width: iw, height: ih };
        const s = Math.min(frame.width / iw, frame.height / ih);
        return { width: iw * s, height: ih * s };
    }

    /// True when this widget should participate in rendering — layer mode
    /// always returns true; widget-filter mode only renders the matching widget.
    function isWidgetVisible(widget) {
        return !widgetFilterId || (widget && widget.id === widgetFilterId);
    }

    /// Paints the configured ?bg= backdrop over the given rect. No-op when the
    /// param wasn't set so OBS sources stay transparent. Called after every
    /// clearRect that prepares an area for a widget render.
    function paintBackdrop(rect) {
        if (!PREVIEW_BG || !rect || rect.width <= 0 || rect.height <= 0) return;
        ctx.save();
        try {
            ctx.fillStyle = PREVIEW_BG;
            ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
        } finally {
            ctx.restore();
        }
    }

    // ── Per-node manipulator overlay (DaVinci-Fusion style) ──────────────────
    //
    // Visualist's WidgetEditorForm sends SET_MANIPULATOR when a node with
    // spatial parameters is selected; we draw handles on top of the layer
    // canvas and mutate the node's Attributes locally during drag. On mouseup
    // we POST ATTR_CHANGED back so the C# document picks up the final values
    // (and auto-sync persists them to disk → OBS / canvas blit catch up).
    //
    // Coordinate space: handles live in WIDGET pixel space (same as
    // widget.rect dimensions). The overlay canvas matches the layer canvas's
    // intrinsic dimensions, so 1px on the layer canvas == 1px on the overlay.
    // Mouse events come in CSS pixels; we scale by canvas.width / clientWidth.

    let manipulatorState = null; // { widgetId, triggerName, nodeId, nodeKind, attrs }
    let manipulatorDrag  = null; // { handleId, startX, startY, startAttrs, scaleX, scaleY, shiftKey }
    let manipulatorOverlay = null;

    function ensureManipulatorOverlay() {
        if (manipulatorOverlay) return manipulatorOverlay;
        const o = document.createElement('canvas');
        o.id = 'manipulator';
        // Stack on top of #layer inside #stage. Letterbox math in compositor's
        // canvas style applies CSS pixel dimensions only; we keep the overlay
        // styled identically so handle px == widget px.
        o.style.position = 'absolute';
        o.style.pointerEvents = 'auto';
        o.style.cursor = 'default';
        const stage = document.getElementById('stage');
        if (stage) stage.appendChild(o); else document.body.appendChild(o);
        manipulatorOverlay = o;
        o.addEventListener('mousedown', onManipMouseDown);
        o.addEventListener('mousemove', onManipMouseMove);
        window.addEventListener('mouseup', onManipMouseUp);
        return o;
    }

    function syncManipulatorOverlaySize() {
        if (!manipulatorOverlay || !canvas) return;
        // Match intrinsic + CSS dimensions to the layer canvas so handle
        // coordinates line up 1:1 with widget pixels.
        // The overlay stays in LOGICAL space — it draws handles in widget px and
        // maps the mouse via overlay.width / clientWidth. Mirror the LOGICAL
        // canvas size, NOT the device backing store, so the manipulator's
        // coordinate math (currentOverlayScale, hit-testing, drag) is byte-for-
        // byte unchanged regardless of dpr.
        manipulatorOverlay.width  = logicalW;
        manipulatorOverlay.height = logicalH;
        // Position the overlay precisely OVER the layer canvas. The canvas
        // sits in a flex-centered #stage, so we anchor the overlay using the
        // canvas's measured offset rather than re-deriving the layout math.
        const rect = canvas.getBoundingClientRect();
        manipulatorOverlay.style.left   = rect.left   + 'px';
        manipulatorOverlay.style.top    = rect.top    + 'px';
        manipulatorOverlay.style.width  = rect.width  + 'px';
        manipulatorOverlay.style.height = rect.height + 'px';
        manipulatorOverlay.style.zIndex = '50';
    }

    // ── DOM-overlay track, part 1 of 2 (WebOverlay.Custom sinks) ─────────────
    //
    // The track has TWO consumers: this one, and V15's Player.Embed further down, which
    // reuses every layout helper declared here (ensureDomOverlayContainer /
    // _alignDomOverlayContainer / _placeOverlayHost) and keeps its own entry map because
    // its teardown has to stop timers and destroy an iframe, not just detach a <div>.
    //
    // Path B: a WebOverlay.Custom node mounts author HTML+CSS as a LIVE DOM element in
    // #dom-overlay, positioned over the widget rect. The browser composites + animates
    // it natively over the canvas — nothing is rasterised into the Image pipeline (which
    // is exactly why real CSS animation works, and why an overlay cannot be Blend/Mask-
    // composited with canvas image nodes). Each host gets its own Shadow root so the
    // author's <style> is scoped and can't leak across widgets or into the page.
    //
    // The node's String input sockets are injected as named CSS custom properties
    // (--<socketName>) on the host and refreshed every render; the socket NAMES are the
    // CSS variable names (renameable in the editor). We rewrite innerHTML / the <style>
    // ONLY when the source text changes — resetting them would restart CSS animations
    // every render — while custom-property updates are cheap and never restart anims.
    //
    // CSP NOTE (the header itself lives in HUDServer.ServeLayerHtmlAsync — do NOT try to
    // express policy from here): the shadow <style> is inline style, so this track only
    // works because the shipped `default-src 'self'` is paired with
    // `style-src 'self' 'unsafe-inline'`. Dropping 'unsafe-inline' (or moving to a
    // per-render nonce) breaks every WebOverlay on the page.
    const _webOverlays = new Map(); // `${widgetId}::${nodeId}` -> { host, shadow, styleEl, contentEl, lastHtml, lastCss, rect }

    // Base shadow CSS: fill the host, stay transparent, and let the author content fill
    // the widget rect. Author CSS (scoped to the shadow root) is appended after this.
    const _WEB_OVERLAY_BASE_CSS =
        ':host{display:block;width:100%;height:100%;overflow:hidden;background:transparent;}' +
        '.phx-overlay-content{width:100%;height:100%;}';

    function _webOverlayKey(widgetId, nodeId) { return String(widgetId) + '::' + String(nodeId); }

    // Sanitise a socket name into a valid CSS custom-property identifier tail (letters,
    // digits, - and _; everything else → '-'). Empty / all-invalid → '' so we never emit `--`.
    function _cssVarName(raw) {
        const s = String(raw == null ? '' : raw).trim().replace(/[^A-Za-z0-9_-]/g, '-');
        return s.replace(/^-+/, '') === '' ? '' : s;
    }

    function ensureDomOverlayContainer() {
        let el = document.getElementById('dom-overlay');
        if (!el) {
            // index.html ships #dom-overlay statically; this lazy path only covers a
            // stripped/legacy page so the feature degrades instead of throwing.
            el = document.createElement('div');
            el.id = 'dom-overlay';
            const stage = document.getElementById('stage');
            (stage || document.body).appendChild(el);
        }
        return el;
    }

    // Align #dom-overlay to the on-screen canvas rect. Cheap; called per overlay render
    // and on window resize so the track tracks the letterboxed canvas. Reads the CACHED
    // canvas rect (canvasScreenRect) — measuring here would force a layout recalc on
    // every animated frame; see the cache comment at the top of the file.
    function _alignDomOverlayContainer(container) {
        if (!canvas) return 1;
        const rect = canvasScreenRect();
        container.style.left   = rect.left   + 'px';
        container.style.top    = rect.top    + 'px';
        container.style.width  = rect.width  + 'px';
        container.style.height = rect.height + 'px';
        return logicalW > 0 ? (rect.width / logicalW) : 1;
    }

    // Place a host in the canvas's LOGICAL coordinate space, scaled to the on-screen
    // canvas. Host is sized to LOGICAL widget px (so author CSS works in authored px)
    // and CSS-scaled by `scale` from its top-left, positioned at the scaled rect origin.
    function _placeOverlayHost(host, rr, scale) {
        host.style.left      = (rr.x * scale) + 'px';
        host.style.top       = (rr.y * scale) + 'px';
        host.style.width     = rr.width  + 'px';
        host.style.height    = rr.height + 'px';
        host.style.transform = 'scale(' + scale + ')';
    }

    // Reposition every mounted overlay (window resize / letterbox change).
    function syncWebOverlayLayout() {
        if (_webOverlays.size === 0) return;
        const container = ensureDomOverlayContainer();
        const scale = _alignDomOverlayContainer(container);
        for (const [, entry] of _webOverlays) {
            const rr = entry.rect || { x: 0, y: 0, width: logicalW, height: logicalH };
            _placeOverlayHost(entry.host, rr, scale);
        }
    }

    /// Mount / refresh the DOM overlay for a WebOverlay.Custom sink node. `ev` is the
    /// active Evaluator (for _evalStringSocket on the slot inputs).
    async function evalWebOverlay(ev, widget, node) {
        const container = ensureDomOverlayContainer();
        const key = _webOverlayKey(widget.id, node.Id);
        let entry = _webOverlays.get(key);
        if (!entry) {
            const host = document.createElement('div');
            host.className = 'phx-overlay-host';
            const shadow = host.attachShadow ? host.attachShadow({ mode: 'open' }) : null;
            const styleEl = document.createElement('style');
            const contentEl = document.createElement('div');
            contentEl.className = 'phx-overlay-content';
            if (shadow) { shadow.appendChild(styleEl); shadow.appendChild(contentEl); }
            else        { host.appendChild(styleEl);   host.appendChild(contentEl); } // no-shadow fallback
            container.appendChild(host);
            entry = { host, shadow, styleEl, contentEl, lastHtml: null, lastCss: null, rect: null };
            _webOverlays.set(key, entry);
        }

        // Position (store the logical rect for resize-time re-placement). Copy the
        // values into the entry's OWN rect object rather than aliasing widget.rect,
        // and mutate it in place instead of allocating a fresh literal — this runs
        // once per frame under the rAF widget animator.
        const rr = widgetRenderRect(widget);
        const er = entry.rect || (entry.rect = { x: 0, y: 0, width: 0, height: 0 });
        er.x      = rr.x;
        er.y      = rr.y;
        er.width  = rr.width;
        er.height = rr.height;
        const scale = _alignDomOverlayContainer(container);
        _placeOverlayHost(entry.host, er, scale);

        // Author CSS + HTML — rewrite ONLY on change so CSS animations don't restart.
        const css  = String(attr(node, 'Css',  '') || '');
        const html = String(attr(node, 'Html', '') || '');
        if (entry.lastCss  !== css)  { entry.styleEl.textContent = _WEB_OVERLAY_BASE_CSS + '\n' + css; entry.lastCss  = css;  }
        if (entry.lastHtml !== html) { entry.contentEl.innerHTML  = html;                              entry.lastHtml = html; }

        // Inject each String input socket as a named CSS custom property (--<name>).
        for (const sock of (node.Sockets || [])) {
            if (!sock || sock.Type !== 0) continue; // 0 = Input
            const varName = _cssVarName(sock.Name);
            if (!varName) continue;
            let val = '';
            try { val = await ev._evalStringSocket(node, sock.Name, ''); }
            catch (e) { console.warn('[Visualist] WebOverlay slot eval failed:', sock.Name, e); }
            entry.host.style.setProperty('--' + varName, String(val == null ? '' : val));
        }
    }

    // Remove overlays owned by `widget` whose node isn't in the current trigger
    // (trigger switch / node deletion). `activeNodeIds` = overlay nodes that just rendered.
    function reconcileWidgetOverlays(widget, activeNodeIds) {
        const prefix = String(widget.id) + '::';
        for (const [key, entry] of _webOverlays) {
            if (key.indexOf(prefix) !== 0) continue;
            const nodeId = key.slice(prefix.length);
            if (!activeNodeIds.has(nodeId)) _removeOverlayEntry(key, entry);
        }
    }

    // Drop overlays for widgets no longer present in the layer at all.
    function sweepWebOverlays(validWidgetIds) {
        for (const [key, entry] of _webOverlays) {
            const wid = key.slice(0, key.indexOf('::'));
            if (!validWidgetIds.has(wid)) _removeOverlayEntry(key, entry);
        }
    }

    function _removeOverlayEntry(key, entry) {
        try { if (entry && entry.host && entry.host.parentNode) entry.host.parentNode.removeChild(entry.host); }
        catch { /* ignore */ }
        _webOverlays.delete(key);
    }

    // ── V15 — iframe player track (Player.Embed sinks) ────────────────────────
    //
    // The SECOND consumer of #dom-overlay, and the only one that mounts a document we
    // did not write. It reuses that track's whole layout contract verbatim —
    // ensureDomOverlayContainer / _alignDomOverlayContainer / _placeOverlayHost, the
    // logical-px host sized and transform-scaled from top-left — and adds nothing to it.
    //
    // ★ THE LIMIT, STATED ONCE. A cross-origin iframe is composited by the BROWSER. It
    // cannot be drawn into the canvas, cannot be faded/masked/transformed by an Image
    // node, and cannot be interleaved in z-order with canvas widgets: #dom-overlay is one
    // layer, fixed above every canvas widget (z 10) and below #manipulator (z 50). So a
    // player owns its whole widget rect, full stop. This is a property of cross-origin
    // embedding, not a gap — the same wall that made WebSource a URL-fetched IMAGE source
    // instead of an iframe.
    //
    // ★ pointer-events STAYS none, inherited from the track. Not an oversight and not a
    // thing to "fix" for the player: an OBS overlay is a render surface nobody clicks, and
    // both feeds run with the native controls off, so there is nothing to click anyway.
    // Scoping pointer-events:auto onto a player host would put a click target over the
    // author's canvas in the Visualist preview for zero gain.
    //
    // ★ UNPROVEN SUBSTRATE — read this before debugging. Nothing in this codebase has ever
    // mounted an iframe on this track, and nobody has confirmed that a youtube-nocookie
    // frame LOADS and AUTOPLAYS inside a real OBS Browser Source. Everything below is
    // therefore written to fail LOUDLY: every failure paints a legible on-host card AND
    // pushes one TRIGGER_DIAGNOSTIC, so a dead embed reads as a stated failure in the
    // streamer's System Log rather than as a black rectangle. If the pre-flight comes back
    // negative, this block, the CSP frame-src line and the Player preset are the whole of
    // what gets struck.
    //
    // The failure surfaces, and WHICH QUESTION each one answers — this is the list the
    // pre-flight is read off, so it is worth stating exactly:
    //
    //   queue-fed leg (YouTube)
    //     player_embed_not_loaded  the frame never navigated at all — network / OBS.
    //     player_embed_blocked     the frame navigated but no player ever answered our
    //                              handshake: a CSP / X-Frame-Options refusal or an error
    //                              page. ★ This is the verdict the load EVENT cannot give,
    //                              because `load` fires for all three of those.
    //     player_embed_not_playing a real player answered and then did not start after we
    //                              asked it to — autoplay refused. ONLY reachable after a
    //                              handshake, so it can no longer be pinned on a hard block.
    //     player_embed_error       the player itself refused the video (onError).
    //
    //   trigger-fed leg (Twitch clip) — no cross-origin protocol exists, so the detectable
    //   failures are refused BEFORE a frame is mounted rather than waited for:
    //     player_clip_bad_slug        the wired value is not a clip.
    //     player_clip_no_parent_host  no `parent=` host, which Twitch requires.
    //     player_clip_not_loaded      the frame never navigated. Note what this does NOT
    //                                 cover: Twitch's own refusal page is a 200 that loads
    //                                 fine and is invisible from out here.
    const _playerEmbeds = new Map(); // `${widgetId}::${nodeId}` -> entry (shape at _playerHostCreate)

    // The two embed origins, and the ONLY two. They are the literal counterpart of
    // HUDServer's `frame-src` directive: a URL this builder cannot produce is a URL that
    // policy would reject anyway, and vice versa. Author input never reaches an origin —
    // only a video id or a clip slug does, and both are pattern-validated first.
    const _YT_EMBED_ORIGIN    = 'https://www.youtube-nocookie.com';
    const _TWITCH_CLIP_ORIGIN = 'https://clips.twitch.tv';

    // Two-stage watchdog, and the ORDER is what makes the pre-flight answerable from a log
    // line: "no player ever came up in that frame" (blocked outright — CSP, X-Frame-Options,
    // an error page, or no network) is a different verdict from "a player came up and then
    // did not start" (autoplay refused, video unavailable), and they need different fixes.
    // Stage one must therefore fire FIRST and must be settled by the HANDSHAKE, not by the
    // iframe's load event — see _armPlayerLoadWatchdog for why the load event answers the
    // wrong question.
    const _PLAYER_LOAD_TIMEOUT_MS  = 6000;
    const _PLAYER_READY_TIMEOUT_MS = 12000;   // mirrors PlayerEmbedSinkNode.ReadyTimeoutMs

    // Re-post the YouTube `listening` handshake until the player answers. The frame can be
    // loaded a beat before its player bundle is ready to receive, so a single post at
    // onload is genuinely lossy; the interval is cleared by the first reply.
    const _YT_HANDSHAKE_INTERVAL_MS = 500;

    function _playerKey(widgetId, nodeId) { return String(widgetId) + '::' + String(nodeId); }

    /// The 11-character YouTube id, or '' when the value is not one. Hub already parsed and
    /// validated the id before publishing it, so this is a shape check against a corrupted
    /// channel value rather than a parser — the point is that nothing author-controlled can
    /// ever be pasted into the middle of an embed URL.
    function _ytVideoId(raw) {
        const s = String(raw == null ? '' : raw).trim();
        return /^[A-Za-z0-9_-]{11}$/.test(s) ? s : '';
    }

    /// A Twitch clip SLUG out of any of the FOUR shapes an author might wire in: a
    /// clips.twitch.tv/<slug> URL, a clips.twitch.tv/embed?clip=<slug> URL, a
    /// twitch.tv/<channel>/clip/<slug> URL, or a bare slug.
    ///
    /// Deliberately returns the slug and not a URL: the caller rebuilds the URL against the
    /// one hard-coded origin, so the `twitch.tv/<channel>/clip/` web-page form is REWRITTEN
    /// to the embed host rather than loaded (that page is not an embed and would render a
    /// full Twitch site chrome inside the widget, if the CSP even let it through — which it
    /// would not, twitch.tv is not in frame-src).
    ///
    /// Slug charset is the same one Twitch mints and the same one CSP-safe: letters,
    /// digits, '-' and '_'. Anything else yields '' and the player stays down, which is the
    /// correct failure for a typo — and, since the caller now paints a card for it, a
    /// STATED failure rather than a silent one.
    function _twitchClipSlug(raw) {
        let s = String(raw == null ? '' : raw).trim();
        if (!s) return '';

        // ★ The EMBED form goes FIRST, before the query is stripped, and that ordering is the
        // whole of it. `clips.twitch.tv/embed?clip=<slug>` is what the Twitch share dialog's
        // Embed tab hands a streamer, and its slug lives in the QUERY. Strip the query first
        // and the path-tail match below resolves the entire URL to the literal slug "embed",
        // which mounts a frame for a clip that does not exist — a black rect for a value that
        // was perfectly correct.
        const q = s.match(/[?&]clip=([^&#]+)/i);
        if (q) {
            let v = q[1];
            try { v = decodeURIComponent(v); } catch { /* a malformed escape stays as typed */ }
            return /^[A-Za-z0-9_-]{1,120}$/.test(v) ? v : '';
        }

        // Strip a query/fragment so `?t=1` on a pasted link doesn't land in the slug.
        const cut = s.search(/[?#]/);
        if (cut >= 0) s = s.slice(0, cut);
        const m = s.match(/(?:^|\/)clip\/([^/]+)$/i)      // twitch.tv/<channel>/clip/<slug>
               || s.match(/clips\.twitch\.tv\/([^/]+)$/i); // clips.twitch.tv/<slug>
        if (m) s = m[1];
        else if (s.indexOf('/') >= 0) return '';           // some other URL — not a clip
        return /^[A-Za-z0-9_-]{1,120}$/.test(s) ? s : '';
    }

    /// `autoplay` MIRRORS THE TRANSPORT THE CHANNEL IS ASKING FOR, and it is a parameter
    /// rather than a constant because of one specific failure: a hard-coded `autoplay=1`
    /// makes a PAUSED track start playing the moment the frame mounts. That is not a rare
    /// interleave — every OBS source restart, scene-collection reload and browser-source
    /// refresh remounts the frame, and the queue-fed leg only tears the frame down for
    /// `idle`/empty, so a track the streamer deliberately held comes back playing while Hub,
    /// the panel and the overlay all still say Paused.
    function _ytEmbedUrl(videoId, autoplay) {
        // enablejsapi is what opens the postMessage channel this file speaks directly.
        // The YouTube IFrame API *script* is deliberately NOT loaded: script-src stays
        // 'self', and the wire protocol below is the same one that script would use.
        // origin= is required for the player to accept our commands.
        //
        // enablejsapi is independent of autoplay, so the handshake still lands on a frame
        // mounted with autoplay=0 — which is what lets the explicit pauseVideo/setVolume
        // flush below run for a held track exactly as it does for a playing one.
        return _YT_EMBED_ORIGIN + '/embed/' + encodeURIComponent(videoId)
            + '?enablejsapi=1&autoplay=' + (autoplay ? '1' : '0')
            + '&playsinline=1&controls=0&disablekb=1'
            + '&rel=0&modestbranding=1&fs=0&iv_load_policy=3'
            + '&origin=' + encodeURIComponent(window.location.origin);
    }

    function _twitchClipEmbedUrl(slug) {
        // `parent` must name the host embedding the frame or Twitch refuses to play. The
        // overlay is always served from loopback, so this is window.location.hostname —
        // which is exactly what compositor.js dials its own socket against.
        return _TWITCH_CLIP_ORIGIN + '/embed?clip=' + encodeURIComponent(slug)
            + '&parent=' + encodeURIComponent(window.location.hostname)
            + '&autoplay=true&muted=false';
    }

    /// TRIGGER_DIAGNOSTIC from the WATCHDOG path — a third builder beside the
    /// message-driven one (sendTriggerDiagnostic) and the evaluation one
    /// (sendEvalDiagnostic), and it has to be its own.
    ///
    /// Both of those read `triggerContext` for attribution, which is correct inside a
    /// render and WRONG here: a watchdog fires from a setTimeout seconds later, by which
    /// point triggerContext describes whatever rendered most recently — very possibly
    /// another widget. Attributing an embed failure to an unrelated trigger is a lying
    /// diagnostic, so this one reads the ids the entry captured when it mounted.
    ///
    /// No-throw and best-effort, like its siblings: a diagnostic must never be able to
    /// break the thing it is reporting on.
    function _sendPlayerDiagnostic(entry, reason, detail) {
        try {
            if (!socket || socket.readyState !== WebSocket.OPEN) return;
            socket.send(JSON.stringify({
                type:        'TRIGGER_DIAGNOSTIC',
                layerId,
                triggerName: (entry && entry.triggerName) || '',
                widgetId:    (entry && entry.widgetId) || null,
                reason:      reason || '',
                detail:      detail ? String(detail).slice(0, 240) : '',
            }));
        } catch (_) { /* best-effort */ }
    }

    /// MEDIA_ENDED — the ONE upward frame this track originates, and the only widget→Hub
    /// message in the product that is not a diagnostic or an ack.
    ///
    /// `seq` is the songrequest.play_token the widget was given, NOT a page-local counter.
    /// That is the whole reason two OBS sources on one layer cannot double-advance: both
    /// read the same token off the same live channel, so both report the SAME
    /// (videoId, seq) pair and Hub collapses them. A per-page counter would be two
    /// different values and would skip a track.
    ///
    /// CONTROL class, like the VISUAL_COMPLETE ack and unlike the node-trace frame: a
    /// queue advance is not a frame-rate diagnostic that goes stale in flight. If Hub is
    /// briefly unreachable the report is worth delivering on reconnect — the token cannot
    /// have moved while Hub was down, so it still matches; and if Hub RESTARTED, its
    /// session queue is empty and the report is a harmless no-op.
    ///
    /// Not sent from an editor client. Hub refuses it there anyway
    /// (IsInboundAllowedFromEditorClient), so this gate is the cheap half of a pair rather
    /// than the security boundary — it keeps a preview pane from spending a rate-limited
    /// refusal log line on every track.
    /// sendSocket's boolean is deliberately not consulted, the same way sendLiveHello and
    /// sendComplete do not consult it: for a CONTROL-class frame `false` does not mean
    /// "lost", it means "queued for the reconnect flush", which is the designed outcome and
    /// carries nothing a caller could act on. Branching on it would only tempt a future
    /// reader into a retry the outbox already performs.
    function sendMediaEnded(entry) {
        if (!entry || CLIENT_KIND === 'editor') return;
        sendSocket({
            type:     'MEDIA_ENDED',
            layerId,
            widgetId: entry.widgetId || '',
            videoId:  entry.videoId || '',
            seq:      entry.playToken || 0,
        }, { control: true });
    }

    // One page-level listener for every embed. YouTube posts its player events to the
    // parent window, so there is nothing per-frame to attach to.
    //
    // Attached on the first load and detached again once the last entry is gone, rather than
    // left standing for the life of the page: `message` is a GLOBAL event, so every frame on
    // the page and any opener reaches this handler, and a layer that no longer holds a single
    // player has no business running an origin check plus a Map walk on each of them. The pair
    // is symmetric — _playerLoad ensures, _removePlayerEntry releases.
    let _playerMessageListenerAttached = false;

    function _ensurePlayerMessageListener() {
        if (_playerMessageListenerAttached) return;
        _playerMessageListenerAttached = true;
        window.addEventListener('message', _onPlayerFrameMessage);
    }

    function _releasePlayerMessageListenerIfIdle() {
        if (!_playerMessageListenerAttached || _playerEmbeds.size > 0) return;
        _playerMessageListenerAttached = false;
        try { window.removeEventListener('message', _onPlayerFrameMessage); }
        catch { /* a listener we cannot detach is harmless — the flag stays false and
                   _ensurePlayerMessageListener would re-add it, which is idempotent for an
                   identical (type, handler) pair. */ }
    }

    function _onPlayerFrameMessage(ev) {
        // Origin check FIRST and unconditionally. `message` is a global event: every frame
        // on the page, and any opener, can post to us. Only the YouTube embed origin has
        // anything to say here, and accepting a payload from anywhere else would let a
        // stray frame fake an "ended" and skip the streamer's track.
        if (!ev || ev.origin !== _YT_EMBED_ORIGIN) return;

        let entry = null;
        for (const [, e] of _playerEmbeds) {
            if (e.iframe && e.iframe.contentWindow === ev.source) { entry = e; break; }
        }
        if (!entry) return;

        let msg;
        try { msg = (typeof ev.data === 'string') ? JSON.parse(ev.data) : ev.data; }
        catch { return; }
        if (!msg || typeof msg !== 'object') return;

        // ★ ANY reply at all is THE readiness signal, and it is the only real one this side
        // of the wire has. A reply proves a YouTube player is running in that frame and is
        // listening to us — which the iframe element's own `load` event does NOT: `load`
        // fires for a CSP-blocked navigation, for an X-Frame-Options refusal and for a
        // browser error page, so gating readiness on it reports a hard block as a healthy
        // mount. Stop re-posting the handshake, retire the load watchdog, and push whatever
        // transport/volume the channel has been asking for while nobody was listening.
        _clearYtHandshake(entry);
        if (!entry.handshook) {
            entry.handshook = true;
            _clearPlayerLoadWatchdog(entry);
            _playerFlushTransport(entry);
        }

        if (msg.event === 'onError') {
            // A fatal embed error IS an end of media: this track will never play, in this
            // frame or any other. Reporting it as ended is what keeps a region-blocked or
            // embedding-disabled request from wedging the queue forever — and it can only
            // happen once per selection, because the advance moves play_token and the
            // latch below is keyed on it. The card and the diagnostic still say WHY, so
            // "the queue skipped my song" is never silent.
            //
            // Capped on READ, like _sendPlayerDiagnostic caps its detail at 240: `info` is a
            // string this page did not author, and it lands in `card.textContent` — which the
            // diagnostic's own cap does not protect, because the card is a different surface.
            const code = String(msg.info == null ? '' : msg.info).slice(0, 32);
            _playerFail(entry, 'player_embed_error',
                'The embed refused this video (YouTube error ' + code + ') — commonly '
                + 'embedding disabled by the uploader, region-blocked, or removed.');
            _playerReportEnded(entry);
            return;
        }

        if (msg.event === 'onStateChange') {
            const state = Number(msg.info);
            if (state === 1) {                 // PLAYING — the substrate works.
                entry.sawPlaying = true;
                _clearPlayerWatchdogs(entry);
                _playerHideCard(entry);
            } else if (state === 0) {          // ENDED
                _playerReportEnded(entry);
            }
        }
    }

    /// Sends whatever the live channel is currently asking for, and ONLY on change.
    ///
    /// ★ THIS IS THE FUNCTION THE WIDGET'S RENDER CADENCE MADE NECESSARY. The desired
    /// transport and volume are recorded on the entry by the eval below; ACTUALLY sending
    /// them has to wait for the player to answer, and the answer arrives on a postMessage
    /// — not on a render. A Player.Embed widget consumes no clock and carries no keyframes,
    /// so it is never in the animator loop: its only re-render is a `songrequest.*` patch,
    /// and the 2 s republish coalesces to nothing while the values are unchanged. So there
    /// was, genuinely, no next render to come back on — the initial setVolume and the
    /// initial transport were never dispatched at all, and every track played at YouTube's
    /// default volume. Hence two call sites, both idempotent: the handshake reply above
    /// (first chance) and the eval below (every later change).
    function _playerFlushTransport(entry) {
        if (!entry || entry.mode !== 'songrequest' || !entry.iframe) return;
        if (!entry.handshook) return;   // nothing is listening yet — the reply will call back

        if (entry.wantTransport && entry.lastTransport !== entry.wantTransport) {
            entry.lastTransport = entry.wantTransport;
            if (entry.wantTransport === 'pause') {
                _ytCommand(entry, 'pauseVideo');
                // A held track is not expected to report PLAYING, so the second-stage
                // watchdog must not be left standing to accuse it of failing to.
                _clearPlayerPlayingWatchdog(entry);
            } else {
                // Cleared with the ask, not merely at load: sawPlaying answers "has it played
                // since the last time we told it to", so a RESUME that silently fails is
                // caught by the same deadline as a first play. Left standing from an earlier
                // track it would make every resume unwatchable.
                entry.sawPlaying = false;
                _ytCommand(entry, 'playVideo');
                _armPlayerPlayingWatchdog(entry);
            }
        }

        if (entry.wantVolume >= 0 && entry.lastVolume !== entry.wantVolume) {
            entry.lastVolume = entry.wantVolume;
            _ytCommand(entry, 'setVolume', [entry.wantVolume]);
        }
    }

    /// Send MEDIA_ENDED at most once per loaded selection. The latch is on the ENTRY and is
    /// reset by _playerLoad, so a track re-selected later (the same video requested twice)
    /// reports again — which is correct, because Hub gave it a new play_token.
    function _playerReportEnded(entry) {
        if (!entry || entry.mode !== 'songrequest' || entry.ended) return;
        if (!entry.videoId) return;
        entry.ended = true;
        sendMediaEnded(entry);
    }

    function _postYt(entry, message) {
        try {
            if (!entry || !entry.iframe || !entry.iframe.contentWindow) return;
            entry.iframe.contentWindow.postMessage(JSON.stringify(message), _YT_EMBED_ORIGIN);
        } catch (_) { /* frame torn down mid-post */ }
    }

    function _ytCommand(entry, func, args) {
        _postYt(entry, { event: 'command', func, args: args || [], id: entry.frameId, channel: 'widget' });
    }

    function _startYtHandshake(entry) {
        _clearYtHandshake(entry);
        const post = () => _postYt(entry, { event: 'listening', id: entry.frameId, channel: 'widget' });
        post();
        entry.ytArmTimer = setInterval(post, _YT_HANDSHAKE_INTERVAL_MS);
    }

    function _clearYtHandshake(entry) {
        if (entry && entry.ytArmTimer !== null) {
            try { clearInterval(entry.ytArmTimer); } catch { /* ignore */ }
            entry.ytArmTimer = null;
        }
    }

    function _clearPlayerLoadWatchdog(entry) {
        if (entry && entry.loadTimer !== null) {
            try { clearTimeout(entry.loadTimer); } catch { /* ignore */ }
            entry.loadTimer = null;
        }
    }

    function _clearPlayerPlayingWatchdog(entry) {
        if (entry && entry.readyTimer !== null) {
            try { clearTimeout(entry.readyTimer); } catch { /* ignore */ }
            entry.readyTimer = null;
        }
    }

    function _clearPlayerWatchdogs(entry) {
        _clearPlayerLoadWatchdog(entry);
        _clearPlayerPlayingWatchdog(entry);
    }

    function _playerShowCard(entry, text) {
        if (!entry || !entry.card) return;
        entry.card.textContent = text;
        entry.card.style.display = 'flex';
    }

    function _playerHideCard(entry) {
        if (!entry || !entry.card) return;
        entry.card.style.display = 'none';
    }

    /// The visible failure path. Paints the card AND pushes one TRIGGER_DIAGNOSTIC, because
    /// the two reach different people: the card is what the streamer sees on the overlay
    /// they are broadcasting, the log line is what survives to be read afterwards. Latched
    /// per load so a repeating condition cannot spam either surface.
    function _playerFail(entry, reason, text) {
        if (!entry || entry.failed) return;
        entry.failed = true;
        _clearPlayerWatchdogs(entry);
        _clearYtHandshake(entry);
        _playerShowCard(entry, text);
        _sendPlayerDiagnostic(entry, reason, text);
    }

    /// STAGE ONE — "did a player ever come up in that frame?", armed at every load.
    ///
    /// ★ The verdict is the HANDSHAKE, not the iframe's `load` event, and that distinction is
    /// the whole point of this sprint's pre-flight. `load` is the classic non-signal for an
    /// embed: it fires for a CSP `frame-src` refusal, for an X-Frame-Options block and for a
    /// plain browser error page, all of which are exactly the outcomes the pre-flight has to
    /// be able to name. Gating on it reported a hard block as a healthy mount and let the
    /// SECOND stage take the verdict twelve seconds later — mislabelling "OBS blocked the
    /// embed outright" as "autoplay was refused", i.e. answering the pre-flight wrongly from
    /// the one log line built to answer it.
    ///
    /// `load` is still read, as the DISCRIMINATOR between the two shapes of failure: a frame
    /// that never even navigated is a different fix from a frame that navigated to something
    /// which is not a YouTube player.
    function _armPlayerLoadWatchdog(entry) {
        _clearPlayerLoadWatchdog(entry);
        entry.loadTimer = setTimeout(() => {
            entry.loadTimer = null;

            if (entry.mode === 'songrequest') {
                if (entry.handshook) return;
                if (!entry.frameLoaded) {
                    _playerFail(entry, 'player_embed_not_loaded',
                        'The embed never loaded. In OBS this usually means the Browser Source '
                        + 'cannot reach the embed host; check the network and that the source is '
                        + 'not running with a cached page.');
                } else {
                    _playerFail(entry, 'player_embed_blocked',
                        'The frame loaded but no YouTube player ever answered — the embed was '
                        + 'REFUSED, not merely slow. A blocked frame (CSP frame-src, '
                        + 'X-Frame-Options) and an error page both load successfully, so this '
                        + 'is what a refusal looks like from outside: a document that is not a '
                        + 'player.');
                }
                return;
            }

            // ── The clip leg's failure surface ────────────────────────────────────────
            // A Twitch clip embed speaks no cross-origin protocol at all, so there is no
            // handshake to wait for and `load` is genuinely the last thing knowable about
            // it. Stated plainly rather than dressed up: this fires ONLY when the frame
            // never navigated. Twitch refusing the embed over a `parent=` mismatch serves a
            // 200 refusal PAGE, which loads fine and is indistinguishable from a playing
            // clip here — which is why the two things that ARE detectable (an unusable
            // parent host, and a value that is not a clip at all) are refused up front in
            // _evalPlayerClip instead of being left to this timer.
            if (entry.frameLoaded) return;
            _playerFail(entry, 'player_clip_not_loaded',
                'The clip embed never loaded. In OBS this usually means the Browser Source '
                + 'cannot reach clips.twitch.tv; check the network and that the source is not '
                + 'running with a cached page.');
        }, _PLAYER_LOAD_TIMEOUT_MS);
    }

    /// STAGE TWO — "we asked it to play; did it?", armed by the transport flush when a
    /// playVideo actually goes out, and cleared when the player reports PLAYING.
    ///
    /// Armed from the flush rather than from the load for two reasons that are really one:
    /// a track the channel says is PAUSED is never asked to play, so accusing it of failing
    /// to start would be a watchdog firing on correct behaviour; and a pause→play made later
    /// is a fresh ask that deserves its own deadline. The queue-fed leg only — the clip leg
    /// has no playback signal to wait for.
    function _armPlayerPlayingWatchdog(entry) {
        if (!entry || entry.mode !== 'songrequest') return;
        _clearPlayerPlayingWatchdog(entry);
        entry.readyTimer = setTimeout(() => {
            entry.readyTimer = null;
            if (entry.sawPlaying) return;
            _playerFail(entry, 'player_embed_not_playing',
                'The embed loaded but never started playing. The usual cause is the '
                + 'browser refusing autoplay with sound; the track is selected in Phoenix '
                + 'but nothing is being heard.');
        }, _PLAYER_READY_TIMEOUT_MS);
    }

    /// Tear the frame down and return the entry to its idle shape. Removing the element
    /// (rather than pointing it at about:blank) is what actually stops a background player
    /// and releases its network connection, and it keeps the frame lifecycle unambiguous:
    /// an entry either has a live frame for a known srcKey or has none.
    function _playerUnload(entry) {
        if (!entry) return;
        _clearPlayerWatchdogs(entry);
        _clearYtHandshake(entry);
        if (entry.iframe) {
            try { if (entry.iframe.parentNode) entry.iframe.parentNode.removeChild(entry.iframe); }
            catch { /* ignore */ }
            entry.iframe = null;
        }
        entry.srcKey       = '';
        entry.videoId      = '';
        entry.playToken    = 0;
        entry.frameLoaded  = false;
        entry.handshook    = false;
        entry.sawPlaying   = false;
        entry.ended        = false;
        entry.failed       = false;
        entry.lastTransport = '';
        entry.lastVolume   = -1;
        // The DESIRED transport/volume are channel state, not frame state — but an entry with
        // no frame wants nothing, and clearing them here keeps a torn-down entry from handing
        // a stale ask to the next load's flush. The eval re-derives both before every use.
        entry.wantTransport = '';
        entry.wantVolume   = -1;
        _playerHideCard(entry);
        entry.host.style.display = 'none';
    }

    /// (Re)load the frame for `srcKey`. Every per-load latch resets here, which is why the
    /// same video selected twice in a row still reports its own end: Hub minted a new
    /// play_token, so this is a new srcKey and a new load.
    ///
    /// ★ The caller must set entry.wantTransport / entry.wantVolume AFTER this returns —
    /// _playerUnload clears them, and the flush that consumes them runs off the handshake
    /// reply, which cannot land before this function has returned.
    function _playerLoad(entry, srcKey, url) {
        _playerUnload(entry);
        entry.srcKey = srcKey;
        entry.host.style.display = 'block';

        const frame = document.createElement('iframe');
        frame.className = 'phx-player-frame';
        // allow= is what lets the embed autoplay with sound and go fullscreen-free inside
        // the widget rect. It is a REQUEST, not a guarantee — whether OBS's CEF honours
        // autoplay is precisely the unproven half of this substrate.
        frame.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture');
        frame.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        frame.setAttribute('frameborder', '0');
        frame.setAttribute('scrolling', 'no');
        frame.setAttribute('title', 'Phoenix player embed');
        frame.addEventListener('load', () => {
            entry.frameLoaded = true;
            if (entry.mode === 'songrequest') _startYtHandshake(entry);
        });
        frame.src = url;
        entry.iframe = frame;
        entry.host.appendChild(frame);

        _ensurePlayerMessageListener();
        _armPlayerLoadWatchdog(entry);
    }

    function _playerHostCreate(container, widget, node) {
        const host = document.createElement('div');
        host.className = 'phx-overlay-host phx-player-host';
        host.style.display = 'none';   // idle until something is actually selected
        const card = document.createElement('div');
        card.className = 'phx-player-card';
        card.style.display = 'none';
        host.appendChild(card);
        container.appendChild(host);
        return {
            host, card,
            iframe:      null,
            rect:        null,
            mode:        '',
            widgetId:    (widget && widget.id) || '',
            nodeId:      (node && node.Id) || '',
            // Echoed through the YouTube handshake so its replies carry an id we chose.
            // Frame matching is done on contentWindow identity, which is authoritative;
            // this is for readability when inspecting frames by hand.
            frameId:     'phx-' + String((widget && widget.id) || '') + '-' + String((node && node.Id) || ''),
            triggerName: '',
            srcKey:      '',
            videoId:     '',
            playToken:   0,
            frameLoaded: false,
            // THE readiness signal: a YouTube player in this frame answered our handshake.
            // Distinct from frameLoaded on purpose — see _armPlayerLoadWatchdog.
            handshook:   false,
            sawPlaying:  false,
            ended:       false,
            failed:      false,
            // What the channel is ASKING for, recorded by the eval and dispatched by
            // _playerFlushTransport once a player is listening. Split from last* because the
            // ask and the last thing actually sent are separated in time by the handshake.
            wantTransport: '',
            wantVolume:  -1,
            lastTransport: '',
            lastVolume:  -1,
            ytArmTimer:  null,
            loadTimer:   null,
            readyTimer:  null,
        };
    }

    /// Mount / refresh the iframe player for a Player.Embed sink node. Visited exactly like
    /// evalWebOverlay: the visit IS the side effect, and it must stay cheap because a
    /// keyframed or animated neighbour puts this on the rAF path.
    ///
    /// EDITOR CLIENTS MOUNT NO FRAME. A Visualist preview pane and the hidden
    /// thumbnail-capture host both render this node, and a real embed there would autoplay
    /// a stranger's music into the author's headphones while they drag widgets around —
    /// and, for the capture host, into a screenshot. They get the card instead, which also
    /// tells the author what the widget is rather than showing them an empty rect.
    async function evalPlayerEmbed(ev, widget, node) {
        const container = ensureDomOverlayContainer();
        const key = _playerKey(widget.id, node.Id);
        let entry = _playerEmbeds.get(key);
        if (!entry) {
            entry = _playerHostCreate(container, widget, node);
            _playerEmbeds.set(key, entry);
        }
        entry.triggerName = (triggerContext && triggerContext.triggerName) || '';

        // Position first, and in place — same rect discipline as evalWebOverlay: copy into
        // the entry's own object rather than aliasing widget.rect, and never allocate here.
        const rr = widgetRenderRect(widget);
        const er = entry.rect || (entry.rect = { x: 0, y: 0, width: 0, height: 0 });
        er.x      = rr.x;
        er.y      = rr.y;
        er.width  = rr.width;
        er.height = rr.height;
        const scale = _alignDomOverlayContainer(container);
        _placeOverlayHost(entry.host, er, scale);

        const mode = stripQuotes(String(attr(node, 'Source', 'songrequest') || 'songrequest'))
            .trim().toLowerCase() === 'clip' ? 'clip' : 'songrequest';
        if (entry.mode !== mode) { _playerUnload(entry); entry.mode = mode; }

        if (CLIENT_KIND === 'editor') {
            entry.host.style.display = 'block';
            _playerShowCard(entry, mode === 'clip'
                ? 'Player (clip) — the embed runs in OBS only'
                : 'Player (song request) — the embed runs in OBS only');
            return;
        }

        // Awaited, not fired and forgotten: the clip leg resolves a wired String socket,
        // so dropping the promise would both lose its rejection and let the caller's
        // reconcile pass run against a frame that had not been decided yet.
        if (mode === 'clip') { await _evalPlayerClip(ev, entry, node); return; }
        _evalPlayerSongRequest(entry);
    }

    /// Queue-fed: an ORDINARY SUBSCRIBER to songrequest.*. It owns no queue state and never
    /// decides what plays next — it renders whatever Hub selected and reports the end.
    ///
    /// Reload is keyed on play_token, not on video_id, and that is the whole reason the key
    /// exists Hub-side: the same song requested twice in a row is a NEW selection with an
    /// identical id, and a video-id compare would resume the finished one instead of
    /// starting it.
    function _evalPlayerSongRequest(entry) {
        const state   = liveTextOf(liveRenderableValue('songrequest.state')).trim().toLowerCase();
        const videoId = _ytVideoId(liveTextOf(liveRenderableValue('songrequest.video_id')));
        const token   = liveNumberOf(liveRenderableValue('songrequest.play_token'));
        const volume  = liveNumberOf(liveRenderableValue('songrequest.volume'));

        // Idle, switched off, or a channel value we refuse to trust — all one outcome: no
        // frame and nothing on screen. An idle player must be INVISIBLE, not a card; the
        // overlay is on air.
        if (!videoId || state === 'idle' || state === '') { _playerUnload(entry); return; }

        // The ask, derived before anything is mounted so the mount can honour it. `paused`
        // must NOT autoplay: the frame is remounted by every OBS source restart and every
        // browser-source refresh, and this leg only tears it down for idle/empty — so a
        // hard-coded autoplay=1 resurrected a held track as playing music while Hub, the
        // panel and the overlay all still read Paused.
        const wantTransport = (state === 'paused') ? 'pause' : 'play';
        const wantVolume    = Math.max(0, Math.min(100, Math.round(volume)));

        const srcKey = 'yt:' + videoId + ':' + token;
        if (entry.srcKey !== srcKey) {
            _playerLoad(entry, srcKey, _ytEmbedUrl(videoId, wantTransport === 'play'));
            entry.videoId   = videoId;
            entry.playToken = token;
        }

        // Recorded AFTER the load, which resets them (see _playerLoad), and dispatched by the
        // shared flush — which is also called from the handshake reply. That second call site
        // is not belt-and-braces: this widget is not a clock consumer and carries no
        // keyframes, so it is never in the animator loop and there is NO per-frame render
        // here. Its only render trigger is a songrequest.* patch, and the 2 s republish
        // coalesces to nothing while the values hold, so a `return` taken before the transport
        // block was never revisited — which is exactly how every track ended up playing at
        // YouTube's default volume with no setVolume ever sent.
        entry.wantTransport = wantTransport;
        entry.wantVolume    = wantVolume;
        _playerFlushTransport(entry);
    }

    /// Trigger-fed one-shot: a shoutout plays its clip once. There is no queue and no
    /// upward signal — nothing on the Hub side owns a clip sequence to advance, and a
    /// Twitch clip embed exposes no cross-origin end event to report even if there were.
    /// The widget's own trigger hold governs how long it stays up: when the hold expires
    /// the widget reverts to onStartup, this node stops rendering, and
    /// reconcileWidgetPlayers tears the frame down.
    ///
    /// ★ ITS FAILURE SURFACE, and why it is up here rather than in a watchdog. A Twitch clip
    /// embed answers nothing cross-origin, and Twitch's own refusal (a `parent=` that does not
    /// name the embedding host) is served as a 200 PAGE — it loads successfully and is
    /// indistinguishable from a playing clip from outside the frame. So a timer can only ever
    /// report "the frame never navigated", and the two failures a streamer actually hits have
    /// to be refused BEFORE a frame is mounted:
    ///
    ///   • the value is not a clip — a typo, a plain twitch.tv channel link, a VOD URL. Silent
    ///     until now: the widget simply stayed black, with nothing in any log.
    ///   • there is no usable `parent` host. Twitch REQUIRES it, and the overlay is always
    ///     served over http(s) from loopback, so an empty hostname means the page is being
    ///     shown from somewhere no clip embed can ever work (a file:// or about:blank host).
    ///     Mounting anyway would render Twitch's refusal page inside the streamer's overlay.
    async function _evalPlayerClip(ev, entry, node) {
        let raw = '';
        try { raw = await ev._evalQuotedStringSocket(node, 'Clip', ''); }
        catch (e) { console.warn('[Visualist] Player.Embed Clip eval failed:', e); }

        // Empty is not a failure — it is an unconfigured or not-yet-triggered widget, and the
        // overlay is on air. Only a value that was MEANT to be a clip and is not gets a card.
        const trimmed = String(raw == null ? '' : raw).trim();
        if (!trimmed) { _playerUnload(entry); return; }

        // Both refusals latch through srcKey — the entry's "what am I currently showing", a
        // failure card being as much a thing shown as a frame is. _playerFail's own `failed`
        // flag cannot carry this, because _playerUnload clears it; without the srcKey latch a
        // keyframed shoutout widget would repaint the card and re-send its diagnostic on every
        // animator frame. A changed value produces a different latch and is reported afresh.
        const slug = _twitchClipSlug(trimmed);
        if (!slug) {
            const badKey = 'clip-bad:' + trimmed;
            if (entry.srcKey === badKey) return;
            _playerUnload(entry);
            entry.srcKey = badKey;
            entry.host.style.display = 'block';
            _playerFail(entry, 'player_clip_bad_slug',
                'Not a Twitch clip: "' + trimmed.slice(0, 80) + '". Wire a clip slug, a '
                + 'clips.twitch.tv link, or a twitch.tv/<channel>/clip/<slug> link.');
            return;
        }

        const parentHost = String(window.location.hostname || '').trim();
        if (!parentHost) {
            const noParentKey = 'clip-noparent:' + slug;
            if (entry.srcKey === noParentKey) return;
            _playerUnload(entry);
            entry.srcKey = noParentKey;
            entry.host.style.display = 'block';
            _playerFail(entry, 'player_clip_no_parent_host',
                'Twitch clip embeds require a parent host, and this page has none. Point the '
                + 'OBS Browser Source at the http://127.0.0.1 overlay URL Phoenix serves rather '
                + 'than at a local file.');
            return;
        }

        const srcKey = 'clip:' + slug;
        if (entry.srcKey !== srcKey) _playerLoad(entry, srcKey, _twitchClipEmbedUrl(slug));
    }

    // Remove players owned by `widget` whose node isn't in the current trigger (trigger
    // switch / node deletion). Mirrors reconcileWidgetOverlays exactly.
    function reconcileWidgetPlayers(widget, activeNodeIds) {
        const prefix = String(widget.id) + '::';
        for (const [key, entry] of _playerEmbeds) {
            if (key.indexOf(prefix) !== 0) continue;
            const nodeId = key.slice(prefix.length);
            if (!activeNodeIds.has(nodeId)) _removePlayerEntry(key, entry);
        }
    }

    // Drop players for widgets no longer present in the layer at all.
    function sweepPlayerEmbeds(validWidgetIds) {
        for (const [key, entry] of _playerEmbeds) {
            const wid = key.slice(0, key.indexOf('::'));
            if (!validWidgetIds.has(wid)) _removePlayerEntry(key, entry);
        }
    }

    function _removePlayerEntry(key, entry) {
        // Unload BEFORE detaching the host: the timers and the frame are what actually keep
        // a torn-down player alive, and dropping the host alone would leave an orphaned
        // interval posting into a detached window forever.
        try { _playerUnload(entry); } catch { /* ignore */ }
        try { if (entry && entry.host && entry.host.parentNode) entry.host.parentNode.removeChild(entry.host); }
        catch { /* ignore */ }
        _playerEmbeds.delete(key);
        _releasePlayerMessageListenerIfIdle();
    }

    // Reposition every mounted player (window resize / letterbox change). Same contract as
    // syncWebOverlayLayout, and it must run even for an entry whose host is hidden: the
    // host is placed once here and shown later without re-measuring.
    function syncPlayerEmbedLayout() {
        if (_playerEmbeds.size === 0) return;
        const container = ensureDomOverlayContainer();
        const scale = _alignDomOverlayContainer(container);
        for (const [, entry] of _playerEmbeds) {
            const rr = entry.rect || { x: 0, y: 0, width: logicalW, height: logicalH };
            _placeOverlayHost(entry.host, rr, scale);
        }
    }

    function setManipulator(msg) {
        manipulatorState = {
            widgetId:    msg.widgetId    || '',
            triggerName: msg.triggerName || '',
            nodeId:      msg.nodeId      || '',
            nodeKind:    msg.nodeKind    || '',
            attrs:       Object.assign({}, msg.attrs || {}),
        };
        // Live typed-edit bridge (Majo): SET_MANIPULATOR is re-posted by the WinUI
        // editor whenever a node's body-pill / Inspector value is committed (not only
        // on selection). Mirror those attrs into the active node in the in-memory
        // `layer` and re-render the widget so a typed TranslateX/Y / Scale / Rotation
        // (or any attribute) updates the rendered content — previously this only moved
        // the handle overlay, so the pill/Inspector "did nothing" in the preview while
        // a manipulator DRAG (which mutates the node inline) worked. A pure selection
        // re-post carries the same values the node already holds ⇒ changed=false ⇒ no
        // spurious re-render.
        const node = getActiveNode();
        if (node && msg.attrs) {
            node.Attributes = node.Attributes || {};
            let changed = false;
            for (const k of Object.keys(msg.attrs)) {
                if (String(node.Attributes[k] ?? '') !== String(msg.attrs[k] ?? '')) {
                    node.Attributes[k] = msg.attrs[k];
                    changed = true;
                }
            }
            if (changed) requestRerenderActiveWidget();
        }
        const o = ensureManipulatorOverlay();
        o.style.display = 'block';
        syncManipulatorOverlaySize();
        drawManipulator();
    }

    function clearManipulator() {
        manipulatorState = null;
        manipulatorDrag  = null;
        if (manipulatorOverlay) {
            manipulatorOverlay.style.display = 'none';
            const c = manipulatorOverlay.getContext('2d');
            c.clearRect(0, 0, manipulatorOverlay.width, manipulatorOverlay.height);
        }
    }

    function getActiveWidget() {
        if (!manipulatorState || !layer || !layer.widgets) return null;
        return layer.widgets.find(w => w.id === manipulatorState.widgetId) || null;
    }

    function getActiveNode() {
        const w = getActiveWidget();
        if (!w || !manipulatorState) return null;
        const trig = (w.triggers || []).find(t => t && t.name === manipulatorState.triggerName);
        if (!trig) return null;
        const nodes = (trig.graph && trig.graph.Nodes) || [];
        return nodes.find(n => n.Id === manipulatorState.nodeId) || null;
    }

    /// Compute the current CSS->widget pixel scale factor from the overlay's
    /// live layout. Used at pointer-down to *snapshot* the mapping so a mid-drag
    /// letterbox/resize can't shift the coordinate space under the cursor (D3).
    function currentOverlayScale() {
        const rect = manipulatorOverlay.getBoundingClientRect();
        return {
            sx: manipulatorOverlay.width  / Math.max(1, rect.width),
            sy: manipulatorOverlay.height / Math.max(1, rect.height),
            left: rect.left,
            top:  rect.top,
        };
    }

    /// Convert a CSS-pixel mouse position relative to the overlay into the
    /// widget pixel coordinate space the handles live in.
    /// [D3] When a drag is active we reuse the scale captured at pointer-down
    /// (manipulatorDrag.scaleX/Y/left/top) instead of re-measuring the overlay
    /// every event — a mid-drag resize would otherwise change the CSS->widget
    /// mapping and make the handle drift away from the cursor. Hover/hit-test
    /// (no active drag) still measures live so the latest layout is honoured.
    function eventToWidgetPx(evt) {
        if (manipulatorDrag &&
            Number.isFinite(manipulatorDrag.scaleX) &&
            Number.isFinite(manipulatorDrag.scaleY)) {
            const cssX = evt.clientX - manipulatorDrag.left;
            const cssY = evt.clientY - manipulatorDrag.top;
            return { x: cssX * manipulatorDrag.scaleX, y: cssY * manipulatorDrag.scaleY };
        }
        const s = currentOverlayScale();
        const cssX = evt.clientX - s.left;
        const cssY = evt.clientY - s.top;
        return { x: cssX * s.sx, y: cssY * s.sy };
    }

    function onManipMouseDown(evt) {
        if (!manipulatorState) return;
        const widget = getActiveWidget();
        if (!widget) return;
        const def = ManipulatorKinds[manipulatorState.nodeKind];
        if (!def) return;
        const handles = def.getHandles(manipulatorState.attrs, widget.rect);
        // manipulatorDrag is still null here, so eventToWidgetPx measures live
        // — the correct mapping at gesture start, which we then snapshot below.
        const pt = eventToWidgetPx(evt);
        let hit = hitTestHandle(handles, pt);
        // bug #6 — body drag: a press on the bare image AREA (inside the edited
        // frame but off every handle) moves the whole image. Handles are tested
        // first, so a scale/rotate grab is never stolen by the body. Only
        // Image.Transform exposes hitBody (it owns the TranslateX/Y the drag drives).
        if (!hit && def.hitBody && def.hitBody(manipulatorState.attrs, widget.rect, pt)) {
            hit = { id: 'body' };
        }
        if (!hit) return;
        // [D3] Snapshot the CSS->widget scale + overlay origin at pointer-down
        // and reuse it for the whole gesture. eventToWidgetPx reads these while
        // the drag is live, so a mid-drag letterbox/resize can't move the frame.
        const snap = currentOverlayScale();
        manipulatorDrag = {
            handleId:   hit.id,
            startX:     pt.x,
            startY:     pt.y,
            startAttrs: Object.assign({}, manipulatorState.attrs),
            scaleX:     snap.sx,
            scaleY:     snap.sy,
            left:       snap.left,
            top:        snap.top,
            shiftKey:   !!evt.shiftKey, // [D5] live modifier read each move below
        };
        evt.preventDefault();
    }

    function onManipMouseMove(evt) {
        if (!manipulatorState) return;
        const widget = getActiveWidget();
        if (!widget) return;
        const def = ManipulatorKinds[manipulatorState.nodeKind];
        if (!def) return;
        const pt = eventToWidgetPx(evt);
        // Cursor hint: hover detect (cheap — just on drag we set 'grabbing').
        if (manipulatorDrag) {
            manipulatorOverlay.style.cursor = 'grabbing';
            // [D5] Track Shift live so aspect-lock / rotation-snap engage and
            // release without restarting the drag. applyDrag reads dragMeta.shiftKey.
            manipulatorDrag.shiftKey = !!evt.shiftKey;
            const next = def.applyDrag(manipulatorDrag.handleId, pt, widget.rect, manipulatorDrag.startAttrs, manipulatorDrag);
            if (next) {
                manipulatorState.attrs = Object.assign({}, manipulatorState.attrs, next);
                // Mirror into the live `layer` so the next render sees it.
                const node = getActiveNode();
                if (node) {
                    node.Attributes = node.Attributes || {};
                    Object.assign(node.Attributes, next);
                    requestRerenderActiveWidget();
                }
                drawManipulator();
            }
            evt.preventDefault();
            return;
        }
        // Hover state: 'grab' over a handle, 'move' over the draggable image body
        // (bug #6), default elsewhere.
        const handles = def.getHandles(manipulatorState.attrs, widget.rect);
        if (hitTestHandle(handles, pt)) {
            manipulatorOverlay.style.cursor = 'grab';
        } else if (def.hitBody && def.hitBody(manipulatorState.attrs, widget.rect, pt)) {
            manipulatorOverlay.style.cursor = 'move';
        } else {
            manipulatorOverlay.style.cursor = 'default';
        }
    }

    function onManipMouseUp(evt) {
        if (!manipulatorDrag) return;
        const finalAttrs = manipulatorState ? manipulatorState.attrs : {};
        const dragged = manipulatorDrag;
        manipulatorDrag = null;
        if (manipulatorOverlay) manipulatorOverlay.style.cursor = 'default';
        // Send the FINAL attribute set (only the keys that actually changed)
        // back to C# so the LayerDocument picks up the new state and auto-sync
        // persists. JS already mutated the local layer during the drag, so
        // the next render-on-disk-reload will match.
        if (manipulatorState && finalAttrs && dragged.startAttrs) {
            const changed = {};
            for (const k of Object.keys(finalAttrs)) {
                if (String(finalAttrs[k]) !== String(dragged.startAttrs[k] || '')) changed[k] = finalAttrs[k];
            }
            if (Object.keys(changed).length > 0) {
                postAttrChanged(changed);
            }
        }
    }

    function postAttrChanged(changed) {
        if (typeof chrome === 'undefined' || !chrome.webview || !chrome.webview.postMessage) return;
        try {
            chrome.webview.postMessage({
                type:        'ATTR_CHANGED',
                widgetId:    manipulatorState.widgetId,
                triggerName: manipulatorState.triggerName,
                nodeId:      manipulatorState.nodeId,
                attrs:       changed,
            });
        } catch (e) { /* host went away — drop silently */ }
    }

    function requestRerenderActiveWidget() {
        const widget = getActiveWidget();
        if (!widget) return;
        const trig = (widget.triggers || []).find(t => t && t.name === manipulatorState.triggerName)
                  || (widget.triggers || []).find(t => t && t.name === 'onStartup');
        if (!trig) return;
        // Skip the per-widget-lock pump and just re-render this widget. The
        // manipulator only runs in the embedded preview where we want
        // immediate visual feedback.
        renderWidgetTrigger(widget, trig).then(() => {
            // Repaint handles on top after the layer canvas re-rendered.
            drawManipulator();
        }).catch(() => { /* tolerate eval failures during drag */ });
    }

    // [D4] Hit-test the SAME handle objects getHandles returns — those carry
    // the actual DRAWN positions (clamped to widget bounds, rotated to the
    // node's frame). Previously corner/rotate hit-zones could diverge from the
    // dot you see because a separate unclamped/axis-aligned position was used,
    // leaving dead zones. Now draw == hit by construction. We also widen the
    // radius (and tolerate the overlay's CSS<->widget scale, so the felt target
    // matches the visual dot regardless of how down-scaled the preview is) and
    // iterate back-to-front so the topmost handle (e.g. 'rotate' drawn last)
    // wins when zones overlap.
    function hitTestHandle(handles, pt) {
        const r = HANDLE_HIT_RADIUS;
        for (let i = handles.length - 1; i >= 0; i--) {
            const h = handles[i];
            const dx = pt.x - h.x, dy = pt.y - h.y;
            if (dx * dx + dy * dy <= r * r) return h;
        }
        return null;
    }

    // bug #6 — handles were a barely-visible 6px gold dot. Now a larger gold chip
    // with a white contrast halo + soft shadow + a function ICON, so each handle's
    // purpose (scale / rotate / move) reads at a glance even over busy images.
    const HANDLE_RADIUS     = 10;
    // Generous grab zone so handles stay usable on small letterboxed previews
    // where each widget pixel maps to well under one CSS pixel (D4 ergonomics).
    const HANDLE_HIT_RADIUS = 18;
    const HANDLE_FILL       = 'rgba(255, 215, 0, 0.97)';   // Phoenix Controls theme — Selection (gold)
    const HANDLE_STROKE     = 'rgba(20, 20, 20, 0.95)';
    const HANDLE_HALO       = 'rgba(255, 255, 255, 0.85)'; // bug #6 — white ring for contrast on busy images
    const HANDLE_GLYPH      = 'rgba(20, 20, 20, 0.92)';    // bug #6 — dark function icon drawn on the gold chip
    const GUIDE_STROKE      = 'rgba(255, 215, 0, 0.55)';
    const FRAME_STROKE      = 'rgba(255, 215, 0, 0.95)';   // bug #6 — solid bounding frame around the edited image
    const FRAME_SHADOW      = 'rgba(0, 0, 0, 0.55)';

    function drawManipulator() {
        if (!manipulatorOverlay || !manipulatorState) return;
        const widget = getActiveWidget();
        if (!widget) return;
        syncManipulatorOverlaySize();
        const c = manipulatorOverlay.getContext('2d');
        c.clearRect(0, 0, manipulatorOverlay.width, manipulatorOverlay.height);
        const def = ManipulatorKinds[manipulatorState.nodeKind];
        if (!def) return;

        c.save();
        try {
            if (def.drawGuides) def.drawGuides(c, manipulatorState.attrs, widget.rect);
            // bug #6 — solid bounding frame around the edited image, under the handles.
            if (def.drawFrame) def.drawFrame(c, manipulatorState.attrs, widget.rect);
            const handles = def.getHandles(manipulatorState.attrs, widget.rect);
            for (const h of handles) drawHandle(c, h);
        } finally {
            c.restore();
        }
    }

    // bug #6 — a handle is a gold chip with a white contrast halo, a soft shadow,
    // and a function ICON keyed off its id (scale arrows / rotate arrow / move
    // cross). Handles with no known role (crop + mask corners/edges) fall back to a
    // clean chip — still far more visible than the old 6px dot.
    function drawHandle(c, h) {
        const r = HANDLE_RADIUS;
        c.save();
        // Soft shadow so the chip lifts off busy image content.
        c.shadowColor   = FRAME_SHADOW;
        c.shadowBlur    = 4;
        c.shadowOffsetX = 0;
        c.shadowOffsetY = 1;
        c.beginPath();
        c.arc(h.x, h.y, r, 0, Math.PI * 2);
        c.fillStyle = HANDLE_FILL;
        c.fill();
        // Drop the shadow before the rings/glyph so it doesn't smear them.
        c.shadowColor   = 'transparent';
        c.shadowBlur    = 0;
        c.shadowOffsetY = 0;
        // White contrast halo + thin dark inner edge.
        c.lineWidth   = 2;
        c.strokeStyle = HANDLE_HALO;
        c.stroke();
        c.beginPath();
        c.arc(h.x, h.y, r - 1, 0, Math.PI * 2);
        c.lineWidth   = 1;
        c.strokeStyle = HANDLE_STROKE;
        c.stroke();
        // Function icon.
        c.strokeStyle = HANDLE_GLYPH;
        c.fillStyle   = HANDLE_GLYPH;
        c.lineWidth   = 1.6;
        c.lineCap     = 'round';
        c.lineJoin    = 'round';
        const g = r * 0.55; // glyph half-extent
        switch (h.id) {
            case 'corner':
            case 'scale':  drawScaleGlyph(c, h.x, h.y, g);  break;
            case 'rotate': drawRotateGlyph(c, h.x, h.y, g); break;
            case 'pivot':
            case 'body':   drawMoveGlyph(c, h.x, h.y, g);   break;
            default:       /* crop / mask handle — plain chip, no glyph */ break;
        }
        c.restore();
    }

    // Diagonal double-headed arrow ↗↙ — "scale".
    function drawScaleGlyph(c, x, y, g) {
        c.beginPath();
        c.moveTo(x - g, y + g);
        c.lineTo(x + g, y - g);
        c.stroke();
        _arrowHead(c, x + g, y - g, -Math.PI * 0.25, g * 0.85);
        _arrowHead(c, x - g, y + g,  Math.PI * 0.75, g * 0.85);
    }

    // Curved arrow — "rotate".
    function drawRotateGlyph(c, x, y, g) {
        c.beginPath();
        c.arc(x, y, g, Math.PI * 0.4, Math.PI * 1.9);
        c.stroke();
        const a  = Math.PI * 0.4;                       // arc start (leading end)
        const ex = x + Math.cos(a) * g, ey = y + Math.sin(a) * g;
        _arrowHead(c, ex, ey, a + Math.PI * 0.5, g * 0.85); // tangent direction
    }

    // 4-way arrows — "move".
    function drawMoveGlyph(c, x, y, g) {
        c.beginPath();
        c.moveTo(x - g, y); c.lineTo(x + g, y);
        c.moveTo(x, y - g); c.lineTo(x, y + g);
        c.stroke();
        _arrowHead(c, x + g, y,  0,            g * 0.7);
        _arrowHead(c, x - g, y,  Math.PI,      g * 0.7);
        _arrowHead(c, x, y - g, -Math.PI / 2,  g * 0.7);
        _arrowHead(c, x, y + g,  Math.PI / 2,  g * 0.7);
    }

    // Small filled arrowhead, tip at (x,y), pointing along `ang` (radians).
    function _arrowHead(c, x, y, ang, size) {
        const a1 = ang + Math.PI * 0.82;
        const a2 = ang - Math.PI * 0.82;
        c.beginPath();
        c.moveTo(x, y);
        c.lineTo(x + Math.cos(a1) * size, y + Math.sin(a1) * size);
        c.lineTo(x + Math.cos(a2) * size, y + Math.sin(a2) * size);
        c.closePath();
        c.fill();
    }

    function drawRectGuide(c, x, y, w, h) {
        c.save();
        c.strokeStyle = GUIDE_STROKE;
        c.lineWidth   = 1;
        c.setLineDash([4, 4]);
        c.strokeRect(x + 0.5, y + 0.5, Math.max(0, w - 1), Math.max(0, h - 1));
        c.restore();
    }

    function drawCircleGuide(c, cx, cy, r) {
        if (r <= 0) return;
        c.save();
        c.strokeStyle = GUIDE_STROKE;
        c.lineWidth   = 1;
        c.setLineDash([4, 4]);
        c.beginPath();
        c.arc(cx, cy, r, 0, Math.PI * 2);
        c.stroke();
        c.restore();
    }

    function drawLineGuide(c, x1, y1, x2, y2) {
        c.save();
        c.strokeStyle = GUIDE_STROKE;
        c.lineWidth   = 1;
        c.setLineDash([4, 4]);
        c.beginPath();
        c.moveTo(x1, y1);
        c.lineTo(x2, y2);
        c.stroke();
        c.restore();
    }

    // ── Per-kind helpers ──────────────────────────────────────────────────

    // Helpers — read attribute as float with default, write back as string
    // (Attributes is Dictionary<string,string> on the C# side).
    function attrF(attrs, key, def) {
        const v = attrs[key];
        if (v === undefined || v === null || v === '') return def;
        const n = parseFloat(v);
        return Number.isFinite(n) ? n : def;
    }
    function asStr(n) {
        // Round floats to 4 decimals for readable .phxlayer round-trips.
        if (typeof n === 'number') return Math.round(n * 10000) / 10000 + '';
        return String(n);
    }
    function clamp(n, lo, hi) { return Math.min(hi, Math.max(lo, n)); }

    // Per-kind manipulators. Each entry returns:
    //   getHandles(attrs, rect) → [{ id, x, y }] in widget pixels
    //   applyDrag(handleId, pt, rect, startAttrs, dragMeta) → { attrName: stringValue, ... }
    //   drawGuides(ctx, attrs, rect) → optional decorative lines
    const ManipulatorKinds = {

        // ── Image transforms ─────────────────────────────────────────────

        'Image.Scale': {
            getHandles(attrs, rect) {
                const factor = attrF(attrs, 'Factor', 1);
                // Anchor at widget center; handle at top-right of the scaled
                // rect (clamped within widget bounds for hit-testability).
                const halfW = rect.width  * factor / 2;
                const halfH = rect.height * factor / 2;
                const cx = rect.width  / 2;
                const cy = rect.height / 2;
                return [{
                    id: 'scale',
                    x: clamp(cx + halfW, 0, rect.width),
                    y: clamp(cy - halfH, 0, rect.height),
                }];
            },
            drawGuides(c, attrs, rect) {
                const factor = attrF(attrs, 'Factor', 1);
                const w = rect.width * factor, h = rect.height * factor;
                const x = (rect.width - w) / 2, y = (rect.height - h) / 2;
                drawRectGuide(c, x, y, w, h);
            },
            applyDrag(id, pt, rect, startAttrs) {
                // Distance from center / half-diagonal of widget = factor.
                const cx = rect.width / 2, cy = rect.height / 2;
                const dx = pt.x - cx, dy = pt.y - cy;
                const halfDiag = Math.hypot(rect.width / 2, rect.height / 2);
                const factor = clamp(Math.hypot(dx, dy) / Math.max(1, halfDiag) * 2, 0.05, 8);
                return { Factor: asStr(factor) };
            },
        },

        'Image.Transform': {
            getHandles(attrs, rect) {
                const tx = attrF(attrs, 'TranslateX', 0);
                const ty = attrF(attrs, 'TranslateY', 0);
                const sx = attrF(attrs, 'ScaleX', 1);
                const sy = attrF(attrs, 'ScaleY', 1);
                const rot = attrF(attrs, 'Rotation', 0) * Math.PI / 180;
                const cx = rect.width / 2 + tx, cy = rect.height / 2 + ty;
                // Top-right corner of the scaled+rotated bounding rect.
                const halfW = rect.width  * sx / 2;
                const halfH = rect.height * sy / 2;
                const cosR = Math.cos(rot), sinR = Math.sin(rot);
                const cornerLocal = { x:  halfW, y: -halfH };
                const corner = {
                    x: cx + cornerLocal.x * cosR - cornerLocal.y * sinR,
                    y: cy + cornerLocal.x * sinR + cornerLocal.y * cosR,
                };
                // [D2] Rotation handle sits a STABLE distance above the scaled
                // top edge, measured along the node's local up axis. The offset
                // is the top edge's half-height (halfH, follows scale so the
                // handle hugs the box) plus a CONSTANT gap. Crucially the gap is
                // independent of live sx/sy, so the handle no longer leaps
                // outward while you scale (the old `max(w,h)*0.25*max(sx,sy)`
                // term made the rotate handle jump during a scale drag).
                const ROT_HANDLE_GAP = 28; // px above the top edge, scale-stable
                const rotDist = halfH + ROT_HANDLE_GAP;
                const rotPt = {
                    x: cx + sinR * rotDist,
                    y: cy - cosR * rotDist,
                };
                return [
                    { id: 'pivot',  x: cx,         y: cy },
                    { id: 'corner', x: corner.x,   y: corner.y },
                    { id: 'rotate', x: rotPt.x,    y: rotPt.y },
                ];
            },
            drawGuides(c, attrs, rect) {
                const tx = attrF(attrs, 'TranslateX', 0);
                const ty = attrF(attrs, 'TranslateY', 0);
                const cx = rect.width / 2 + tx, cy = rect.height / 2 + ty;
                drawCircleGuide(c, cx, cy, 4);
            },
            // bug #6 — solid bounding frame around the transformed image. The image
            // fills the widget rect (Text.Render + every image source are now frame-
            // sized, bug #2), so its on-screen bounds are the widget rect scaled by
            // (sx,sy), rotated by Rotation, centred at (rect/2 + translate). The
            // 'corner' scale handle sits exactly on this rect's top-right vertex.
            _transformCorners(attrs, rect) {
                const tx  = attrF(attrs, 'TranslateX', 0);
                const ty  = attrF(attrs, 'TranslateY', 0);
                const sx  = attrF(attrs, 'ScaleX', 1);
                const sy  = attrF(attrs, 'ScaleY', 1);
                const rot = attrF(attrs, 'Rotation', 0) * Math.PI / 180;
                const cx  = rect.width / 2 + tx, cy = rect.height / 2 + ty;
                const hw  = rect.width  * sx / 2;
                const hh  = rect.height * sy / 2;
                const cosR = Math.cos(rot), sinR = Math.sin(rot);
                const at = (lx, ly) => ({ x: cx + lx * cosR - ly * sinR, y: cy + lx * sinR + ly * cosR });
                return { cx, cy, hw, hh, rot, corners: [at(-hw, -hh), at(hw, -hh), at(hw, hh), at(-hw, hh)] };
            },
            drawFrame(c, attrs, rect) {
                const f = ManipulatorKinds['Image.Transform']._transformCorners(attrs, rect);
                const p = f.corners;
                c.save();
                c.shadowColor = FRAME_SHADOW;
                c.shadowBlur  = 3;
                c.lineWidth   = 1.5;
                c.strokeStyle = FRAME_STROKE;
                c.beginPath();
                c.moveTo(p[0].x, p[0].y);
                c.lineTo(p[1].x, p[1].y);
                c.lineTo(p[2].x, p[2].y);
                c.lineTo(p[3].x, p[3].y);
                c.closePath();
                c.stroke();
                c.restore();
            },
            // bug #6 — body-drag hit-test: is the point inside the (rotated) image
            // frame? Inverse-rotate into the image's local axes, then test the
            // half-extents. Handles win first in onManipMouseDown, so this only
            // catches grabs on the bare image area.
            hitBody(attrs, rect, pt) {
                const f = ManipulatorKinds['Image.Transform']._transformCorners(attrs, rect);
                const dx = pt.x - f.cx, dy = pt.y - f.cy;
                const cosR = Math.cos(-f.rot), sinR = Math.sin(-f.rot);
                const lx = dx * cosR - dy * sinR;
                const ly = dx * sinR + dy * cosR;
                return Math.abs(lx) <= Math.abs(f.hw) && Math.abs(ly) <= Math.abs(f.hh);
            },
            applyDrag(id, pt, rect, startAttrs, drag) {
                const startTx = attrF(startAttrs, 'TranslateX', 0);
                const startTy = attrF(startAttrs, 'TranslateY', 0);
                const shift   = !!(drag && drag.shiftKey);
                if (id === 'pivot' || id === 'body') {
                    // 'body' is the bug #6 whole-image drag — same translate as the
                    // centre pivot handle, just grabbed from anywhere on the image.
                    const dx = pt.x - drag.startX, dy = pt.y - drag.startY;
                    return { TranslateX: asStr(startTx + dx), TranslateY: asStr(startTy + dy) };
                }
                if (id === 'corner') {
                    // [D1] Scale must respect Rotation. The corner is DRAWN at the
                    // rotated position, so the cursor delta from the pivot is in
                    // SCREEN space — we must rotate it back into the node's LOCAL
                    // (un-rotated) frame before solving sx,sy. Otherwise dragging a
                    // rotated widget's corner pulls along the wrong axes and the
                    // handle slides away from the cursor.
                    const cx = rect.width / 2 + startTx, cy = rect.height / 2 + startTy;
                    const rot = attrF(startAttrs, 'Rotation', 0) * Math.PI / 180;
                    const cosR = Math.cos(rot), sinR = Math.sin(rot);
                    const dx = pt.x - cx, dy = pt.y - cy;
                    // Inverse rotation (by -rot): R(-θ)·(dx,dy). With R(θ) as the
                    // forward rotation used in getHandles, the transpose gives the
                    // local-frame delta.
                    const localDx = dx * cosR + dy * sinR;
                    const localDy = -dx * sinR + dy * cosR;
                    let sx = clamp((Math.abs(localDx) * 2) / Math.max(1, rect.width),  0.05, 8);
                    let sy = clamp((Math.abs(localDy) * 2) / Math.max(1, rect.height), 0.05, 8);
                    // [D5] Shift locks aspect ratio (uniform scale). Use the larger
                    // of the two so the box always grows to reach the cursor.
                    if (shift) {
                        const uniform = Math.max(sx, sy);
                        sx = uniform; sy = uniform;
                    }
                    return { ScaleX: asStr(sx), ScaleY: asStr(sy) };
                }
                if (id === 'rotate') {
                    const cx = rect.width / 2 + startTx, cy = rect.height / 2 + startTy;
                    let ang = Math.atan2(pt.x - cx, -(pt.y - cy)) * 180 / Math.PI;
                    // [D5] Shift snaps rotation to 15° increments. Keep the raw
                    // continuous angle when no modifier is held (unchanged default).
                    if (shift) ang = Math.round(ang / 15) * 15;
                    return { Rotation: asStr(ang) };
                }
                return null;
            },
        },

        'Image.Crop': {
            // [QC50-08] Rect is Vector4 (X, Y, W, H) stored as fractions of the
            // source-image dimensions (0..1) — see the Evaluator's Image.Crop case
            // below and EvalImageCrop on the C# side. The manipulator doesn't know
            // the raw source-image dimensions, so it maps fractions onto the
            // widget rect for display and converts pointer-space drag back to
            // fractions on write. The author-visible attribute therefore stays in
            // fraction space regardless of how big the source image renders.
            // Default "0,0,1,1" reads as no crop (full image).
            getHandles(attrs, rect) {
                const r = parseFractionRect(attrs.Rect, rect);
                return [
                    { id: 'tl',   x: r.x,           y: r.y },
                    { id: 'tr',   x: r.x + r.w,     y: r.y },
                    { id: 'bl',   x: r.x,           y: r.y + r.h },
                    { id: 'br',   x: r.x + r.w,     y: r.y + r.h },
                    { id: 'top',  x: r.x + r.w / 2, y: r.y },
                    { id: 'left', x: r.x,           y: r.y + r.h / 2 },
                    { id: 'right',  x: r.x + r.w,   y: r.y + r.h / 2 },
                    { id: 'bottom', x: r.x + r.w / 2, y: r.y + r.h },
                ];
            },
            drawGuides(c, attrs, rect) {
                const r = parseFractionRect(attrs.Rect, rect);
                drawRectGuide(c, r.x, r.y, r.w, r.h);
            },
            applyDrag(id, pt, rect, startAttrs) {
                const r = parseFractionRect(startAttrs.Rect, rect);
                let { x, y, w, h } = r;
                const right = x + w, bottom = y + h;
                if (id === 'tl' || id === 'top'  || id === 'left')   {}
                if (id === 'tl')   { x = clamp(pt.x, 0, right);  y = clamp(pt.y, 0, bottom); }
                if (id === 'tr')   { y = clamp(pt.y, 0, bottom); w = clamp(pt.x - x, 1, rect.width  - x); }
                if (id === 'bl')   { x = clamp(pt.x, 0, right);  h = clamp(pt.y - y, 1, rect.height - y); }
                if (id === 'br')   { w = clamp(pt.x - x, 1, rect.width  - x); h = clamp(pt.y - y, 1, rect.height - y); }
                if (id === 'top')    { y = clamp(pt.y, 0, bottom); }
                if (id === 'left')   { x = clamp(pt.x, 0, right); }
                if (id === 'right')  { w = clamp(pt.x - x, 1, rect.width  - x); }
                if (id === 'bottom') { h = clamp(pt.y - y, 1, rect.height - y); }
                if (id === 'tl' || id === 'top')  { h = bottom - y; }
                if (id === 'tl' || id === 'left') { w = right - x; }
                // Convert widget-pixel drag result back to source-fraction space
                // for storage. Guards against rect.width/height = 0 to avoid NaN.
                const fx = rect.width  > 0 ? x / rect.width  : 0;
                const fy = rect.height > 0 ? y / rect.height : 0;
                const fw = rect.width  > 0 ? w / rect.width  : 1;
                const fh = rect.height > 0 ? h / rect.height : 1;
                return { Rect: `Vector4(${asStr(fx)},${asStr(fy)},${asStr(fw)},${asStr(fh)})` };
            },
        },

        // No spatial concept — count is just an integer. Skip handles.
        'Image.Tile': {
            getHandles() { return []; },
            applyDrag()  { return null; },
        },

        // ── Mask / shape generators (normalised 0..1 coordinates) ────────
        // Convert: widget-px = norm * widget.{width,height}
        // Conversely: norm = widget-px / widget.{width,height}.

        'Mask.Rectangle': {
            getHandles(attrs, rect) {
                const x = attrF(attrs, 'X', 0)      * rect.width;
                const y = attrF(attrs, 'Y', 0)      * rect.height;
                const w = attrF(attrs, 'Width', 1)  * rect.width;
                const h = attrF(attrs, 'Height', 1) * rect.height;
                return [
                    { id: 'tl', x: x,     y: y },
                    { id: 'tr', x: x + w, y: y },
                    { id: 'bl', x: x,     y: y + h },
                    { id: 'br', x: x + w, y: y + h },
                ];
            },
            drawGuides(c, attrs, rect) {
                const x = attrF(attrs, 'X', 0)      * rect.width;
                const y = attrF(attrs, 'Y', 0)      * rect.height;
                const w = attrF(attrs, 'Width', 1)  * rect.width;
                const h = attrF(attrs, 'Height', 1) * rect.height;
                drawRectGuide(c, x, y, w, h);
            },
            applyDrag(id, pt, rect, startAttrs) {
                let x = attrF(startAttrs, 'X', 0)      * rect.width;
                let y = attrF(startAttrs, 'Y', 0)      * rect.height;
                let w = attrF(startAttrs, 'Width', 1)  * rect.width;
                let h = attrF(startAttrs, 'Height', 1) * rect.height;
                const right = x + w, bottom = y + h;
                if (id === 'tl') { x = clamp(pt.x, 0, right);  y = clamp(pt.y, 0, bottom); w = right - x; h = bottom - y; }
                if (id === 'tr') { y = clamp(pt.y, 0, bottom); w = clamp(pt.x - x, 1, rect.width  - x); h = bottom - y; }
                if (id === 'bl') { x = clamp(pt.x, 0, right);  h = clamp(pt.y - y, 1, rect.height - y); w = right - x; }
                if (id === 'br') { w = clamp(pt.x - x, 1, rect.width  - x); h = clamp(pt.y - y, 1, rect.height - y); }
                return {
                    X: asStr(clamp(x / rect.width,  0, 1)),
                    Y: asStr(clamp(y / rect.height, 0, 1)),
                    Width:  asStr(clamp(w / rect.width,  0, 1)),
                    Height: asStr(clamp(h / rect.height, 0, 1)),
                };
            },
        },

        'Mask.Circle': {
            getHandles(attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = attrF(attrs, 'Radius', 0.25) * Math.min(rect.width, rect.height);
                return [
                    { id: 'center', x: cx, y: cy },
                    { id: 'radius', x: cx + r, y: cy },
                ];
            },
            drawGuides(c, attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = attrF(attrs, 'Radius', 0.25) * Math.min(rect.width, rect.height);
                drawCircleGuide(c, cx, cy, r);
            },
            applyDrag(id, pt, rect, startAttrs) {
                if (id === 'center') {
                    return {
                        CX: asStr(clamp(pt.x / rect.width,  0, 1)),
                        CY: asStr(clamp(pt.y / rect.height, 0, 1)),
                    };
                }
                if (id === 'radius') {
                    const cx = attrF(startAttrs, 'CX', 0.5) * rect.width;
                    const cy = attrF(startAttrs, 'CY', 0.5) * rect.height;
                    const denominator = Math.max(1, Math.min(rect.width, rect.height));
                    const r  = Math.hypot(pt.x - cx, pt.y - cy) / denominator;
                    return { Radius: asStr(clamp(r, 0, 1)) };
                }
                return null;
            },
        },

        'Mask.Ellipse': {
            getHandles(attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const rx = attrF(attrs, 'RadiusX', 0.3) * rect.width;
                const ry = attrF(attrs, 'RadiusY', 0.2) * rect.height;
                return [
                    { id: 'center', x: cx, y: cy },
                    { id: 'rx',     x: cx + rx, y: cy },
                    { id: 'ry',     x: cx,      y: cy + ry },
                ];
            },
            drawGuides(c, attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const rx = attrF(attrs, 'RadiusX', 0.3) * rect.width;
                const ry = attrF(attrs, 'RadiusY', 0.2) * rect.height;
                c.save();
                c.strokeStyle = GUIDE_STROKE;
                c.lineWidth   = 1;
                c.setLineDash([4, 4]);
                c.beginPath();
                c.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
                c.stroke();
                c.restore();
            },
            applyDrag(id, pt, rect, startAttrs) {
                if (id === 'center') {
                    return {
                        CX: asStr(clamp(pt.x / rect.width,  0, 1)),
                        CY: asStr(clamp(pt.y / rect.height, 0, 1)),
                    };
                }
                const cx = attrF(startAttrs, 'CX', 0.5) * rect.width;
                const cy = attrF(startAttrs, 'CY', 0.5) * rect.height;
                if (id === 'rx') return { RadiusX: asStr(clamp(Math.abs(pt.x - cx) / rect.width,  0, 1)) };
                if (id === 'ry') return { RadiusY: asStr(clamp(Math.abs(pt.y - cy) / rect.height, 0, 1)) };
                return null;
            },
        },

        'Mask.LinearGradient': {
            getHandles(attrs, rect) {
                const fx = attrF(attrs, 'FromX', 0)   * rect.width;
                const fy = attrF(attrs, 'FromY', 0.5) * rect.height;
                const tx = attrF(attrs, 'ToX',   1)   * rect.width;
                const ty = attrF(attrs, 'ToY',   0.5) * rect.height;
                return [
                    { id: 'from', x: fx, y: fy },
                    { id: 'to',   x: tx, y: ty },
                ];
            },
            drawGuides(c, attrs, rect) {
                const fx = attrF(attrs, 'FromX', 0)   * rect.width;
                const fy = attrF(attrs, 'FromY', 0.5) * rect.height;
                const tx = attrF(attrs, 'ToX',   1)   * rect.width;
                const ty = attrF(attrs, 'ToY',   0.5) * rect.height;
                drawLineGuide(c, fx, fy, tx, ty);
            },
            applyDrag(id, pt, rect) {
                const nx = clamp(pt.x / rect.width,  0, 1);
                const ny = clamp(pt.y / rect.height, 0, 1);
                if (id === 'from') return { FromX: asStr(nx), FromY: asStr(ny) };
                if (id === 'to')   return { ToX:   asStr(nx), ToY:   asStr(ny) };
                return null;
            },
        },

        'Mask.RadialGradient': {
            getHandles(attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                const ir = attrF(attrs, 'InnerRadius', 0)   * r;
                const or = attrF(attrs, 'OuterRadius', 0.5) * r;
                return [
                    { id: 'center', x: cx, y: cy },
                    { id: 'inner',  x: cx + ir, y: cy },
                    { id: 'outer',  x: cx + or, y: cy },
                ];
            },
            drawGuides(c, attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                drawCircleGuide(c, cx, cy, attrF(attrs, 'InnerRadius', 0)   * r);
                drawCircleGuide(c, cx, cy, attrF(attrs, 'OuterRadius', 0.5) * r);
            },
            applyDrag(id, pt, rect, startAttrs) {
                if (id === 'center') {
                    return {
                        CX: asStr(clamp(pt.x / rect.width,  0, 1)),
                        CY: asStr(clamp(pt.y / rect.height, 0, 1)),
                    };
                }
                const cx = attrF(startAttrs, 'CX', 0.5) * rect.width;
                const cy = attrF(startAttrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                const dist = Math.hypot(pt.x - cx, pt.y - cy) / r;
                if (id === 'inner') return { InnerRadius: asStr(clamp(dist, 0, 1)) };
                if (id === 'outer') return { OuterRadius: asStr(clamp(dist, 0, 1)) };
                return null;
            },
        },

        'Mask.Vignette': {
            getHandles(attrs, rect) {
                const cx = rect.width / 2, cy = rect.height / 2;
                const strength = attrF(attrs, 'Strength', 0.5);
                const r = (1 - strength) * Math.min(rect.width, rect.height) * 0.85;
                return [
                    { id: 'center',   x: cx, y: cy },
                    { id: 'strength', x: cx + r, y: cy },
                ];
            },
            drawGuides(c, attrs, rect) {
                const cx = rect.width / 2, cy = rect.height / 2;
                const strength = attrF(attrs, 'Strength', 0.5);
                drawCircleGuide(c, cx, cy, (1 - strength) * Math.min(rect.width, rect.height) * 0.85);
            },
            applyDrag(id, pt, rect) {
                if (id !== 'strength') return null;
                const cx = rect.width / 2, cy = rect.height / 2;
                const r = Math.hypot(pt.x - cx, pt.y - cy);
                const denominator = Math.max(1, Math.min(rect.width, rect.height) * 0.85);
                const strength = clamp(1 - (r / denominator), 0, 1);
                return { Strength: asStr(strength) };
            },
        },

        'Mask.Star': {
            getHandles(attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                const outer = attrF(attrs, 'OuterRadius', 0.4) * r;
                const inner = attrF(attrs, 'InnerRadius', 0.18) * r;
                return [
                    { id: 'center', x: cx, y: cy },
                    { id: 'outer',  x: cx + outer, y: cy },
                    { id: 'inner',  x: cx, y: cy - inner },
                ];
            },
            drawGuides(c, attrs, rect) {
                const cx = attrF(attrs, 'CX', 0.5) * rect.width;
                const cy = attrF(attrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                drawCircleGuide(c, cx, cy, attrF(attrs, 'OuterRadius', 0.4)  * r);
                drawCircleGuide(c, cx, cy, attrF(attrs, 'InnerRadius', 0.18) * r);
            },
            applyDrag(id, pt, rect, startAttrs) {
                if (id === 'center') {
                    return {
                        CX: asStr(clamp(pt.x / rect.width,  0, 1)),
                        CY: asStr(clamp(pt.y / rect.height, 0, 1)),
                    };
                }
                const cx = attrF(startAttrs, 'CX', 0.5) * rect.width;
                const cy = attrF(startAttrs, 'CY', 0.5) * rect.height;
                const r  = Math.min(rect.width, rect.height);
                const dist = Math.hypot(pt.x - cx, pt.y - cy) / r;
                if (id === 'outer') return { OuterRadius: asStr(clamp(dist, 0, 1)) };
                if (id === 'inner') return { InnerRadius: asStr(clamp(dist, 0, 1)) };
                return null;
            },
        },

        'Mask.Polygon':  vertexShapeManipulator(false),
        'Mask.Bezier':   vertexShapeManipulator(true),
    };

    // Vertex / bezier shapes share the same handle-per-vertex pattern. A
    // bezier vertex carries optional cp1*/cp2* control points which become
    // additional handles. The Vertices attribute is a JSON array string.
    function vertexShapeManipulator(isBezier) {
        return {
            getHandles(attrs, rect) {
                const verts = parseVertices(attrs.Vertices);
                const out = [];
                for (let i = 0; i < verts.length; i++) {
                    const v = verts[i];
                    out.push({ id: `v${i}`, x: v.x * rect.width, y: v.y * rect.height });
                    if (isBezier) {
                        if (Number.isFinite(v.cp1x)) out.push({ id: `v${i}.cp1`, x: v.cp1x * rect.width, y: v.cp1y * rect.height });
                        if (Number.isFinite(v.cp2x)) out.push({ id: `v${i}.cp2`, x: v.cp2x * rect.width, y: v.cp2y * rect.height });
                    }
                }
                return out;
            },
            drawGuides(c, attrs, rect) {
                const verts = parseVertices(attrs.Vertices);
                if (verts.length < 2) return;
                c.save();
                c.strokeStyle = GUIDE_STROKE;
                c.lineWidth   = 1;
                c.setLineDash([4, 4]);
                c.beginPath();
                c.moveTo(verts[0].x * rect.width, verts[0].y * rect.height);
                for (let i = 1; i < verts.length; i++)
                    c.lineTo(verts[i].x * rect.width, verts[i].y * rect.height);
                c.closePath();
                c.stroke();
                c.restore();
            },
            applyDrag(id, pt, rect, startAttrs) {
                const verts = parseVertices(startAttrs.Vertices);
                const m = id.match(/^v(\d+)(?:\.(cp1|cp2))?$/);
                if (!m) return null;
                const idx = parseInt(m[1], 10);
                if (idx < 0 || idx >= verts.length) return null;
                const nx = clamp(pt.x / rect.width,  0, 1);
                const ny = clamp(pt.y / rect.height, 0, 1);
                const v  = Object.assign({}, verts[idx]);
                if (!m[2])              { v.x = nx; v.y = ny; }
                else if (m[2] === 'cp1'){ v.cp1x = nx; v.cp1y = ny; }
                else if (m[2] === 'cp2'){ v.cp2x = nx; v.cp2y = ny; }
                verts[idx] = v;
                return { Vertices: JSON.stringify(verts) };
            },
        };
    }

    function parseVertices(json) {
        if (!json) return [];
        try {
            const v = JSON.parse(json);
            return Array.isArray(v) ? v : [];
        } catch { return []; }
    }

    // [QC50-08] Image.Crop's Rect is canonically stored as fractions (0..1) of
    // the source-image dimensions — see EvalImageCrop in NodeEvaluator.cs and
    // the 'Image.Crop' arm in the Evaluator below. The manipulator runs
    // against widget-rect pixel space, so we scale fractions × widget dims for
    // display. A degenerate or missing value defaults to the full widget rect
    // (= no crop) — a stored zero-area rect is treated as "passthrough" rather
    // than "1×1 pixel".
    //
    // V14 — this replaced a pixel-space parseRect() that survived as a dead
    // function long after QC50-08 moved Rect to fractions. parseRect is gone;
    // this is the ONE Rect parser, and its callers are the three sites below.
    // Both parsers shared the W/H ≤ 0 passthrough rule, so the rule itself is
    // unchanged — only the now-orphaned second copy of it is.
    function parseFractionRect(raw, widgetRect) {
        let fx = 0, fy = 0, fw = 1, fh = 1;
        if (raw && typeof raw === 'string') {
            const m = raw.replace(/[^\d.\-,]/g, '').split(',').map(parseFloat);
            if (m.length === 4 && m.every(Number.isFinite)) {
                fx = m[0]; fy = m[1]; fw = m[2]; fh = m[3];
                if (fw <= 0 || fh <= 0) { fx = 0; fy = 0; fw = 1; fh = 1; }
            }
        }
        return {
            x: fx * widgetRect.width,
            y: fy * widgetRect.height,
            w: fw * widgetRect.width,
            h: fh * widgetRect.height,
        };
    }

    // ── WebSocket ────────────────────────────────────────────────────────────

    // #8 — Reconnect with exponential backoff + bounded outbox buffer.
    //
    // Background: the OBS browser source's HUD socket used to reconnect on a
    // fixed 1500 ms timer with no backoff and no message buffering. Two
    // problems:
    //   1. A long Hub outage hammered the server with reconnect attempts
    //      every 1.5 s, with no jitter / cap.
    //   2. Any outbound state from the compositor (compositor → Hub messages
    //      such as VISUAL_COMPLETE acks) emitted while disconnected was
    //      silently dropped — by the time the socket came back, the message
    //      was gone and the Hub never saw the ack.
    //
    // Fix: exponential backoff capped at 30 s (1.5 → 3 → 6 → 12 → 24 → 30 s),
    // resets to the initial 1.5 s on each successful onopen. Outbound messages
    // sent via sendSocket() while the socket is not open get queued in a
    // bounded ring; the queue flushes on reconnect.
    //
    // QC28-10 — split the single 64-cap drop-oldest outbox into TWO classes so a
    // control-class message (a VISUAL_COMPLETE ack) can't be evicted during a long
    // outage. Control queue has a smaller cap but is flushed first (priority send);
    // the data queue keeps the original drop-oldest-when-full behaviour. The
    // classifier honours an explicit { control: true } option and otherwise
    // default-classifies by message type.
    //
    // ★ V14 — what is actually reachable here today, because the original comment
    // named a scenario that cannot happen. All THREE of sendSocket()'s callers pass
    // { control: true } explicitly (the LIVE_HELLO send, sendComplete, and V15's
    // sendMediaEnded), so:
    //   • _isControlPayload is short-circuited away and never runs, and
    //   • nothing ever enqueues into _outboxData.
    // The "data-class chatter (FPS heartbeats)" the split was written to guard
    // against does NOT go through sendSocket at all — the FPS heartbeat and the
    // TRIGGER_RECEIVED / TRIGGER_DIAGNOSTIC / DEBUG_WIDGET_NODE / TRANSLATE_REQUEST
    // frames all use a bare socket.send() guarded by a readyState check, so they are
    // dropped outright while disconnected and never queued.
    //
    // The data branch and the default classifier are KEPT deliberately, not because
    // they run: they are the defence-in-depth for the real Hub-outage ack-loss bug
    // above. Delete them and the next caller that forgets { control: true } — or
    // switches a bare socket.send to sendSocket — silently loses its frame while
    // disconnected instead of being classified and queued.
    let _reconnectDelayMs = 1500;
    const _outboxControl = []; // priority queue (acks etc.)
    const _outboxData    = []; // drop-oldest queue — currently no producer; see above
    const _outboxControlMax = 32;
    const _outboxDataMax    = 64;
    // The default-classify set, i.e. the types _isControlPayload promotes to control
    // class for a caller that passed no explicit flag. All three entries are frames this
    // overlay really originates: the VISUAL_COMPLETE ack a waiting script blocks on;
    // LIVE_HELLO, the ONE frame that arms the Overlay Live Channel for this layer
    // (which is nevertheless the one control type flushOutbox deliberately DISCARDS;
    // see there); and V15's MEDIA_ENDED, which advances the song-request queue and is
    // therefore worth delivering after a reconnect rather than dropping (see
    // sendMediaEnded for why a late one cannot mis-advance). V14 removed a fourth entry,
    // 'HUB_EVENT': this set classifies OUTBOUND payloads and the overlay never builds a
    // HUB_EVENT frame — there is no inbound arm for one either, so it was a breadcrumb
    // for planned work, not a classification anything could reach.
    const _CONTROL_TYPES = new Set(['VISUAL_COMPLETE', 'LIVE_HELLO', 'MEDIA_ENDED']);

    function _isControlPayload(text) {
        // Cheap front-of-string check. Only run JSON.parse if it looks like an
        // object whose first key is "type" — avoids parsing every heartbeat
        // for nothing. Defensive try/catch because the payload could be any
        // string from a future caller.
        // Unreached today (all three callers pass { control: true }) and kept on
        // purpose: it is the safety net that classifies a future caller correctly, and
        // the only consumer of _CONTROL_TYPES.
        if (typeof text !== 'string' || text.length < 8 || text.charCodeAt(0) !== 123) return false;
        try {
            const obj = JSON.parse(text);
            return !!(obj && obj.type && _CONTROL_TYPES.has(obj.type));
        } catch { return false; }
    }

    function sendSocket(payload, opts) {
        const text = (typeof payload === 'string') ? payload : JSON.stringify(payload);
        if (socket && socket.readyState === WebSocket.OPEN) {
            try { socket.send(text); return true; }
            catch { /* fall through to enqueue */ }
        }
        const isControl = (opts && opts.control === true) || _isControlPayload(text);
        if (isControl) {
            if (_outboxControl.length >= _outboxControlMax) _outboxControl.shift();
            _outboxControl.push(text);
        } else {
            // No producer reaches this branch today — see the V14 note on the outbox
            // declarations. Kept as the correct handling for a future data-class
            // caller, so that caller degrades to drop-oldest rather than to silence.
            if (_outboxData.length >= _outboxDataMax) _outboxData.shift();
            _outboxData.push(text);
        }
        return false;
    }

    /// True for a queued LIVE_HELLO frame. Same cheap front-of-string gate as
    /// _isControlPayload — only parse what looks like an object literal — and only ever run
    /// over the (≤32-entry) control queue once per reconnect, so the parse cost is noise.
    function _isQueuedLiveHello(text) {
        if (typeof text !== 'string' || text.length < 8 || text.charCodeAt(0) !== 123) return false;
        try {
            const obj = JSON.parse(text);
            return !!(obj && obj.type === 'LIVE_HELLO');
        } catch { return false; }
    }

    function flushOutbox() {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        // Drain control queue first so VISUAL_COMPLETE acks are not held up
        // behind a backlog of FPS heartbeats accumulated during the outage.
        while (_outboxControl.length > 0) {
            const text = _outboxControl.shift();
            // Exactly one LIVE_HELLO per connect. Our only caller is socket.onopen, which
            // sends a FORCED hello immediately after this flush, re-derived from the current
            // graph set. A hello queued while the socket was down is therefore redundant at
            // best and STALER at worst — its key set predates anything the layer reloaded to
            // during the outage — and letting it through would put two hellos on the wire,
            // each answered with a whole-store LIVE_SNAPSHOT. Dropping it here (rather than
            // flushing after the forced send) also keeps the stale set from being the LAST
            // one Hub sees, which is what makes it a correctness fix and not just a saving.
            if (_isQueuedLiveHello(text)) continue;
            try { socket.send(text); }
            catch { _outboxControl.unshift(text); return; } // put back, retry on next flush
        }
        while (_outboxData.length > 0) {
            const text = _outboxData.shift();
            try { socket.send(text); }
            catch { _outboxData.unshift(text); return; }
        }
    }

    // Last key set announced to Hub, JSON-encoded for an O(1) equality test. The guard it feeds
    // suppresses a re-announce Hub would answer with a redundant whole-store LIVE_SNAPSHOT —
    // but it is only SAFE for a caller that knows Hub still holds this layer's subscription.
    // Both of today's callers force past it precisely because neither can know that (see
    // sendLiveHello); the guard stays as the correct default for a future caller that
    // re-derives the key set with no Hub-side teardown in between.
    let _liveHelloKeysJson = '';

    /// Announces this overlay's Overlay Live Channel subscription. Hub pushes LIVE_SNAPSHOT /
    /// LIVE_PATCH only to layers it has seen a LIVE_HELLO from, and it arms a HELLO deadline
    /// the moment the socket connects — hence the control-class send.
    ///
    /// { force: true } bypasses the unchanged-key-set guard, and BOTH call sites need it:
    ///  * socket.onopen — Hub clears a layer's subscription when its last socket closes, so a
    ///    reconnect has to re-announce even a byte-identical key set.
    ///  * end of softReloadLayer — a LAYER_RELOADED can arrive after OnLayerRemoved cleared
    ///    the subscription while this socket stayed open. Unforced, the common reload (a rect
    ///    nudge, a colour tweak — key set unchanged) no-ops and the overlay is starved of live
    ///    data for the rest of the socket's life, with no diagnostic either: ClearLayer
    ///    dropped the connection note and the hello-seen latch is sticky.
    function sendLiveHello(opts) {
        const keys  = buildLiveSubscription();
        const json  = JSON.stringify(keys);
        const force = !!(opts && opts.force === true);
        if (!force && liveState.helloSent && json === _liveHelloKeysJson) return;
        _liveHelloKeysJson  = json;
        liveState.helloSent = true;
        sendSocket({ type: 'LIVE_HELLO', proto: 1, keys }, { control: true });
    }

    function connectSocket() {
        // ?client=<kind> is the V6 bolt-on: the ONLY thing distinguishing a live OBS
        // browser source from a Visualist design-time preview on this socket. See
        // CLIENT_KIND for the vocabulary and why 'obs' has to be the default.
        //
        // V13 A3 — and the connect token, appended as a SECOND parameter with `&`. §8.3 writes
        // the shape as `?token=<t>` because it describes the field, not its position: ?client=
        // already owns the leading '?', so a second '?' here would make `token` part of the
        // client value and Hub would read every socket as tokenless. Empty when the served page
        // carried no token (a pre-upgrade cached page) — the parameter is then omitted entirely
        // rather than sent blank, so Hub's tokenless grace arm is the one that fires.
        const url = `ws://${window.location.host}/hud/${encodeURIComponent(layerId)}`
                  + `?client=${encodeURIComponent(CLIENT_KIND)}`
                  + (CONNECT_TOKEN ? `&token=${encodeURIComponent(CONNECT_TOKEN)}` : '');
        socket = new WebSocket(url);
        socket.onopen    = () => {
            setStatus('connected');
            _reconnectDelayMs = 1500; // reset backoff on successful connect
            flushOutbox();
            // Re-arm the live channel. Forced because the previous socket's close dropped
            // this layer's subscription Hub-side, so an unchanged key set still needs saying.
            // This is the ONLY hello this connect puts on the wire: flushOutbox above
            // deliberately discards any LIVE_HELLO the outage queued, whose key set can only
            // be equal to or staler than the one we re-derive right here.
            sendLiveHello({ force: true });
        };
        socket.onclose   = () => {
            setStatus('disconnected — retrying…');
            const delay = _reconnectDelayMs;
            _reconnectDelayMs = Math.min(_reconnectDelayMs * 2, 30000);
            setTimeout(connectSocket, delay);
        };
        socket.onerror   = () => setStatus('socket error');
        socket.onmessage = ev => onMessage(ev.data);
    }

    // Sweep 21 — chrome.webview channel for the Visualist WebView2 preview.
    // Visualist's TimelinePlayback sends SCRUB / PLAY / STOP_PLAY directly via
    // PostWebMessageAsJson to avoid a 30Hz round-trip through the Hub bus. The
    // listener is no-op when the page is loaded outside WebView2 (i.e. in OBS).
    if (typeof chrome !== 'undefined' && chrome.webview && chrome.webview.addEventListener) {
        chrome.webview.addEventListener('message', evt => {
            // PostWebMessageAsJson hands us the JSON-parsed object directly on
            // evt.data; PostWebMessageAsString would give a string. We accept both.
            let msg = evt.data;
            if (typeof msg === 'string') {
                try { msg = JSON.parse(msg); } catch { return; }
            }
            if (!msg || !msg.type) return;
            // SET_MANIPULATOR / CLEAR_MANIPULATOR are handled inline by the
            // manipulator overlay — they don't go through onMessage's
            // RUN_TRIGGER / SCRUB / PLAY pipeline.
            if (msg.type === 'SET_MANIPULATOR')   { setManipulator(msg);   return; }
            if (msg.type === 'CLEAR_MANIPULATOR') { clearManipulator();    return; }
            onMessage(JSON.stringify(msg));
        });
    }

    function onMessage(raw) {
        let msg;
        try { msg = JSON.parse(raw); } catch { return; }

        if (msg.type === 'LAYER_RELOADED') {
            // Hub's LayerWatcher reloaded this layer's .phxlayer. Prefer a SOFT
            // reload — re-fetch /api/layer/<id> and repaint in place — over the old
            // hard window.location.reload() so the OBS/preview surface doesn't flash
            // and the WS / caches / live push state survive. Any failure falls back
            // to the hard reload (today's exact behaviour); softReloadLayer clears
            // _triggerMeta itself (matching L48's "re-run dedupe scan on reload").
            setStatus('reloading…');
            softReloadLayer().catch(err => {
                console.warn('[Visualist] soft reload failed, hard reload:', err);
                try { _triggerMeta.clear(); } catch { }
                window.location.reload();
            });
            return;
        }

        if (msg.type === 'HUD_RELOAD') {
            // V13 §8.3 — Hub classified this socket Untrusted (no token, a token for another
            // layer, or a page it never served) and is offering the one self-heal. The latch is
            // inside _selfHealHardReload, NOT here: this arm can legally be reached again after
            // the reload, and it is the storage latch that makes the second visit a no-op instead
            // of a loop. Deliberately NOT gated on CLIENT_KIND — a design-time preview that lost
            // its provenance is just as broken as an OBS source, and Hub only ever sends this to
            // a socket that actually failed the check.
            _selfHealHardReload(msg.reason);
            return;
        }

        if (msg.type === 'RUN_TRIGGER') {
            // Un-awaited by design (onMessage stays synchronous) — a rejection
            // must not surface as an unhandled promise rejection.
            Promise.resolve(handleRunTrigger(msg))
                .catch(err => console.warn('RUN_TRIGGER failed:', err));
            return;
        }

        // Sweep 21 — design-time scrub/play coming via the WebView2 chrome.webview channel.
        // Production OBS browsers never receive these (Visualist sends only into the
        // embedded WebView2). See LayerPreviewPanel.PostScrub / PostPlay / PostStop.
        if (msg.type === 'SCRUB')     { handleScrub(msg);    return; }
        if (msg.type === 'PLAY')      { handlePlay(msg);     return; }
        if (msg.type === 'STOP_PLAY') {
            handleStopPlay();
            // ★ Stop HOLDS the frame. It no longer clears the latch set, and no longer re-seeds.
            //
            // The whole-set clear + _refreshAnimatorDemand() that used to live here were what
            // produced the snap-to-end-pose: releasing the played widget handed its cursor back
            // to the production clock, and the re-seed then repainted it at a page-load-old
            // time. The two shipped comments in this area promised opposite things — one said
            // stop holds the last played frame, the other said stop returns everything to the
            // ambient clock — and the code did the second while documenting the first.
            //
            // What the clear DID buy was an escape hatch, and that hatch had to be replaced
            // rather than simply dropped. The three signals that release a pin —
            // RUN_TRIGGER for the widget, a SET_ACTIVE_TRIGGER tab switch (re-pins to 0),
            // softReloadLayer on save — are all reachable on the single-widget editor
            // preview and NONE of them is reachable on the whole-LAYER preview: it is
            // never sent SET_ACTIVE_TRIGGER, RUN_TRIGGER needs a production script fire,
            // and a save is not something an author should have to do to un-freeze a
            // widget. A bare playhead drag there would have pinned a widget for the page's
            // life with no way back.
            //
            // So the hatch moved to RELEASE_TIME_CURSOR (below), posted by the transport's
            // STOP button. Pause holds the frame; stop releases. That split is what lets
            // both properties hold at once.
            //
            // (Design-preview only — SCRUB / PLAY / STOP_PLAY arrive solely over the WebView2
            // channel, never on a production OBS source.)
            return;
        }

        if (msg.type === 'RELEASE_TIME_CURSOR') {
            // The escape hatch, bound to the gesture that actually means "I am done
            // scrubbing": the transport's STOP button (WidgetEditorView.OnStopClicked),
            // as distinct from pause.
            //
            // ★ Why this exists as its own message. Pause holds the frame, which is the
            // whole point of the latch surviving STOP_PLAY — but that left the LAYER
            // preview with no reachable release at all. Its three theoretical hatches do
            // not fire there: SET_ACTIVE_TRIGGER is never sent to the whole-layer surface,
            // RUN_TRIGGER needs a production script fire from Hub, and softReloadLayer
            // needs an explicit .phxlayer save. Meanwhile a bare playhead drag pins a
            // widget and _seedWidgetAnimator refuses a pinned widget, so its ambient
            // animation stopped for the page's life with no way back short of reloading —
            // and STOP could not help, because TimelinePlayback.Stop() sets TimeMs = 0,
            // which posts a SCRUB that RE-PINS at frame 0 rather than releasing.
            //
            // Posted AFTER that scrub by the click handler, so it lands last and wins.
            _designTimeClockOwners.clear();
            // Re-seed: the widgets just released have no animator slot (the seed refuses a
            // design-time-owned widget), so nothing would ever schedule them a frame. This
            // is what actually hands their ambient animation back, and it re-arms the loop.
            _refreshAnimatorDemand();
            return;
        }

        // Track C — the embedded single-widget preview switches which trigger it
        // shows when the editor's active trigger tab changes. Record the trigger
        // and render it at t=0 so the pane reflects the edited trigger's start
        // state (instead of the onStartup idle). Production OBS never sends this.
        if (msg.type === 'SET_ACTIVE_TRIGGER') { handleSetActiveTrigger(msg); return; }

        // Live property-panel update (Visualist) — apply rect changes to the
        // in-memory widget and re-render so X/Y/W/H edits in the detail
        // panel reflect in the preview without waiting for a save+reload
        // cycle through LayerWatcher. Production OBS sources never receive
        // this message (Visualist only posts it to the embedded WebView2).
        if (msg.type === 'WIDGET_UPDATE') {
            if (!layer || !layer.widgets || !msg.widgetId) return;
            const w = layer.widgets.find(x => x.id === msg.widgetId);
            if (!w) return;
            // Snapshot the pre-edit rect so the targeted repaint can clear the
            // vacated region (all widgets share one canvas). Track whether the rect
            // / zIndex actually changed so a no-op or name-only push skips repaint.
            const oldRect = w.rect
                ? { x: w.rect.x, y: w.rect.y, width: w.rect.width, height: w.rect.height }
                : null;
            let rectChanged = false, zChanged = false;
            if (msg.rect) {
                w.rect = w.rect || {};
                if (typeof msg.rect.x      === 'number' && msg.rect.x      !== w.rect.x)      { w.rect.x      = msg.rect.x;      rectChanged = true; }
                if (typeof msg.rect.y      === 'number' && msg.rect.y      !== w.rect.y)      { w.rect.y      = msg.rect.y;      rectChanged = true; }
                if (typeof msg.rect.width  === 'number' && msg.rect.width  !== w.rect.width)  { w.rect.width  = msg.rect.width;  rectChanged = true; }
                if (typeof msg.rect.height === 'number' && msg.rect.height !== w.rect.height) { w.rect.height = msg.rect.height; rectChanged = true; }
            }
            if (typeof msg.zIndex === 'number' && msg.zIndex !== w.zIndex) { w.zIndex = msg.zIndex; zChanged = true; }
            if (typeof msg.name   === 'string') w.name = msg.name;

            // #4 — repaint the manipulator overlay after the re-render so the
            // handles aren't wiped by the fresh layer-canvas paint. drawManipulator
            // no-ops when no manipulator is set (OBS / non-preview path). A pure
            // rect change takes the targeted fast path; name-only skips repaint.
            if (rectChanged || zChanged) {
                applyWidgetUpdateRender(w, oldRect, rectChanged, zChanged)
                    .then(() => { drawManipulator(); postRenderAck(w.id); })
                    .catch(err => console.warn('WIDGET_UPDATE rerender failed:', err));
            } else {
                drawManipulator();
            }
            return;
        }

        // The four bespoke live-data arms that used to sit here — CAPTION_UPDATE,
        // TIMER_UPDATE, LOYALTY_UPDATE, COUNTER_UPDATE — are gone. Each was one family's
        // private wire format, its own state object and its own near-identical re-render pass;
        // all four now arrive as keys inside LIVE_SNAPSHOT / LIVE_PATCH below, and the readers
        // resolve them out of liveState. Hub retired the four producers in the same rework, so
        // these arms had nothing left to receive. Do NOT re-add one to "fix" a dark widget: the
        // subscription is derived from the graph, so the thing to check is liveKeysForNode's
        // arm and the LIVE_HELLO it produces.

        if (msg.type === 'TRANSLATE_RESPONSE') {
            const resolver = pendingTranslate.get(msg.reqId);
            if (resolver) {
                pendingTranslate.delete(msg.reqId);
                resolver(msg.translated || '');
            }
            return;
        }

        if (msg.type === 'LIVE_SNAPSHOT') {
            // Shape: { type:"LIVE_SNAPSHOT", seq:8812,
            //          entries:[{ k:"timer.main.progress", v:0.62, s:"active" }] }.
            // Hub's answer to LIVE_HELLO and the channel's authoritative reset point: it carries
            // every store entry matching our subscription, so it REPLACES liveState.entries
            // wholesale and supersedes any patch still in flight. Build the fresh Map first and
            // swap it in, so a malformed frame can't leave a half-cleared store behind.
            //
            // DELIBERATELY NOT seq-guarded — a snapshot ALWAYS applies and RESETS our counter.
            // Hub's seq is a process-static counter with no persistence, so a Hub restart puts it
            // back near 0 while this page (which reconnects via socket.onclose's setTimeout and is
            // never reloaded) still holds a high seq from the previous process. Rejecting a
            // low-seq snapshot would make the overlay discard the authoritative state and then
            // every following patch, painting the DEAD Hub's values as live until the new counter
            // organically climbed past the old high-water mark — and it could not even degrade to
            // "stale", because that verdict only travels inside the frames being dropped.
            // In-process the guard would also never fire here: BuildFrame stamps the global
            // counter at build time, so a snapshot always outranks any patch already applied.
            // Only LIVE_PATCH is an increment and therefore guardable; see the patch arm below.
            const seq = _liveFrameSeq(msg);
            const snapshot = Array.isArray(msg.entries) ? msg.entries : [];
            const fresh = new Map();
            for (const e of snapshot) {
                if (e && typeof e.k === 'string') fresh.set(e.k, _liveEntryOf(e));
            }
            liveState.entries = fresh;
            liveState.seq     = seq;
            // null changedKeys = "everything" — a snapshot invalidates every binding.
            renderLiveConsumers(null).catch(err => console.warn('live snapshot rerender failed:', err));
            return;
        }

        if (msg.type === 'LIVE_PATCH') {
            // Shape: { type:"LIVE_PATCH", seq:8813,
            //          entries:[{ k:"timer.main.progress", v:0.63, s:"active" }] }.
            // OverlayLiveStore's pump coalesces at PumpIntervalMs and only sends keys whose
            // value (or liveness verdict) actually changed, so merge rather than replace and
            // re-render only the widgets bound to THESE keys.
            const seq = _liveFrameSeq(msg);
            if (seq < liveState.seq) return;   // late/duplicate frame — see _liveFrameSeq
            const patch = Array.isArray(msg.entries) ? msg.entries : [];
            const changed = new Set();
            for (const e of patch) {
                if (!e || typeof e.k !== 'string') continue;
                liveState.entries.set(e.k, _liveEntryOf(e));
                changed.add(e.k);
            }
            liveState.seq = seq;
            if (changed.size === 0) return;   // malformed / empty frame — nothing to repaint
            renderLiveConsumers(changed).catch(err => console.warn('live patch rerender failed:', err));
            return;
        }
    }

    /// Sends a TRANSLATE_REQUEST and resolves on the matching TRANSLATE_RESPONSE.
    /// Cached by `${text}|${targetLang}` so repeated frames during a render don't refire.
    function requestTranslation(text, targetLang) {
        if (!text || !targetLang) return Promise.resolve(text || '');
        const key = translateCacheKey(text, targetLang);
        const cached = translateCacheGet(key);
        if (cached !== undefined) return Promise.resolve(cached);
        if (!socket || socket.readyState !== WebSocket.OPEN) return Promise.resolve(text);

        return new Promise(resolve => {
            const reqId = `tr-${nextReqId++}`;
            pendingTranslate.set(reqId, translated => {
                translateCacheSet(key, translated);
                resolve(translated);
            });
            // 5 s safety timeout — if the response never arrives, return the original text.
            setTimeout(() => {
                if (pendingTranslate.has(reqId)) {
                    pendingTranslate.delete(reqId);
                    resolve(text);
                }
            }, 5000);
            try {
                socket.send(JSON.stringify({ type: 'TRANSLATE_REQUEST', reqId, text, targetLang }));
            } catch {
                // Send failed (socket closing mid-frame) — fall back to the
                // original text immediately instead of waiting the 5 s timeout.
                if (pendingTranslate.has(reqId)) { pendingTranslate.delete(reqId); resolve(text); }
            }
        });
    }

    // Trigger lookup: layer JSON stores explicit triggers as 'onTrigger:<name>' (Visualist
    // authoring convention) but Hub's RUN_TRIGGER carries the bare '<name>' from
    // visual.trigger_queued(layer, widget, name). Match either form so 'onStartup' (no
    // prefix) and 'greet' → 'onTrigger:greet' both resolve.
    ///
    /// Both passes skip a null / nameless ELEMENT — `triggers: [null]` survives Hub-side
    /// deserialisation and is served to the browser (see widgetConsumesTime). Mirrors
    /// LayerRuntime.FindTrigger, which guards the same input on the Hub half of this handshake.
    function findTrigger(widget, name) {
        if (!widget || !widget.triggers || !name) return null;
        return widget.triggers.find(t => t && t.name === name)
            || widget.triggers.find(t => t && t.name === 'onTrigger:' + name)
            || null;
    }

    // Diagnostic helpers — emit a structured frame back to Hub so silent
    // early-returns and render errors surface in the Hub System Log without
    // requiring the user to attach OBS DevTools.
    function sendTriggerReceived(msg) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        try {
            socket.send(JSON.stringify({
                type:        'TRIGGER_RECEIVED',
                layerId,
                widgetId:    msg && msg.widgetId,
                triggerName: msg && msg.triggerName,
                waitId:      (msg && msg.waitId) || '',
            }));
        } catch (_) { /* best-effort; never fail a render on diagnostics */ }
    }
    function sendTriggerDiagnostic(msg, reason, detail) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        try {
            socket.send(JSON.stringify({
                type:        'TRIGGER_DIAGNOSTIC',
                layerId,
                widgetId:    msg && msg.widgetId,
                triggerName: msg && msg.triggerName,
                reason:      reason || '',
                detail:      detail ? String(detail).slice(0, 240) : '',
            }));
        } catch (_) { /* best-effort */ }
    }

    /// THE ONE TRIGGER_DIAGNOSTIC frame builder for the EVALUATION path — the diagnostics raised
    /// deep inside a render, which have no inbound `msg` to attribute themselves to and therefore
    /// read the layer and trigger off `triggerContext` instead. Its sibling above is the
    /// message-driven half (handleRunTrigger's early-returns), which has a `msg` in hand.
    ///
    /// ★ WHY A console.warn IS NOT A DIAGNOSTIC HERE. On the production path the page is an OBS
    /// Browser Source with no DevTools attached, and the widget-editor WebView2 preview is worse —
    /// its console goes nowhere at all. So a render-path failure that only warns is byte-for-byte
    /// indistinguishable from silence: a blank widget, an honest-looking State pin, no error on
    /// either side. That is the symptom class several of these reports were filed against, not a
    /// cosmetic gap. The Hub arm (HUDServer.cs, `case "TRIGGER_DIAGNOSTIC"`) logs at
    /// LogLevel.System, i.e. the streamer reads it in the System Log without attaching anything,
    /// and it accepts the frame from an editor client too (IsInboundAllowedFromEditorClient
    /// refuses only VISUAL_COMPLETE and FPS), which is what gives the preview surface a voice.
    ///
    /// `triggerContext.layerId` / `.triggerName` are written by `_renderConsumerPass`, `renderAll`
    /// and `handleRunTrigger` BEFORE any evaluation starts, and `layerId` itself is a page constant
    /// with a 'main' fallback — so neither can be empty by the time an evaluator runs.
    ///
    /// ★ WHAT THE TWO FIELDS ARE ACTUALLY FOR — corrected. `triggerName` is load-bearing: Hub READS
    /// it out of this payload (`ReadString(doc.RootElement, "triggerName")`) and prints it in the
    /// System Log line, so it is the only trigger attribution a reader gets. `layerId` is
    /// INFORMATIONAL: `HandleInboundFromBrowser` takes the layer from the SOCKET ROUTE
    /// (`/hud/<layerId>`, injected by the receive loop precisely so the browser need not repeat
    /// itself) and never reads the payload's copy for any frame type. An earlier version of this
    /// comment claimed Hub DROPS a frame whose payload layerId is empty and that populating the
    /// field was therefore load-bearing — it is not; the `if (string.IsNullOrEmpty(layerId)) return;`
    /// guard it was pointing at tests the ROUTE value, which a `/hud/<id>` socket always has. The
    /// field stays because the envelope is shared with the message-driven sibling above and because a
    /// frame read off the wire is unattributable without it, not because anything consumes it.
    ///
    /// Callers keep their OWN console.warn (the media / args / list explanations differ, and the
    /// console is still the right surface for the long fix-it sentence) and their OWN dedupe latch —
    /// this function is deliberately NOT deduped, because the correct dedupe scope differs per
    /// caller (per node+value, per node+arg, per key) and a shared latch would silence one caller's
    /// first report on another's account.
    ///
    /// EVERY caller must gate its call, and all four now do — the requirement is real because an
    /// evaluator runs per pin per render FRAME, and every latch here is therefore PAGE-scoped rather
    /// than per-render: `_reportedRejectedMediaPaths` (per node+value), `_reportMissingArg`'s
    /// `_reportedMissingArgs` (per node+arg+kind) and `_listParseWarned` (per key, shared by
    /// reportListNotArray and liveListRows' catch). A per-render latch does NOT satisfy this: a
    /// fresh Evaluator is constructed per renderWidgetTrigger, so on the animator path its "once"
    /// resets every frame — which is exactly how `_reportMissingArg` used to push a frame per frame
    /// off a keyframed widget whose `Result.If` arg is absent on every non-trigger render.
    ///
    /// No-throw by construction: a render read must never fault on a diagnostic.
    function sendEvalDiagnostic(reason, detail, widgetId) {
        try {
            if (socket && socket.readyState === WebSocket.OPEN) {
                socket.send(JSON.stringify({
                    type:        'TRIGGER_DIAGNOSTIC',
                    layerId:     triggerContext.layerId,
                    triggerName: triggerContext.triggerName,
                    widgetId:    widgetId || null,
                    reason:      reason || '',
                    detail:      detail ? String(detail).slice(0, 240) : '',
                }));
            }
        } catch (_) { /* best-effort; the eval pass must continue */ }
    }

    // ── V13 A2 — DEBUG_WIDGET_NODE, the widget-graph trace frame ──────────────
    //
    // Architect flashes each node as its script line executes (DEBUG_NODE_EXEC). The widget
    // graph had no equivalent, and it cannot get one built the same way: a script line runs
    // once, whereas renderWidgetTrigger re-runs EVERY ANIMATION FRAME for any keyframed or
    // animated-media widget. A per-node or per-render frame would push traffic at frame rate up
    // the same socket that carries the live channel — 60 frames a second per widget, forever,
    // for a diagnostic nobody asked for.
    //
    // So the unit is the ACTIVATION, not the render. The three genuine activation sites —
    // handleRunTrigger, its idle revert, and handleSetActiveTrigger, i.e. exactly the three that
    // bump the audio activation generation — ARM a widget; the next render of that widget spends
    // the arm and sends ONE frame listing every node its evaluation walked. Animator frames,
    // scrub frames, live-patch passes, WIDGET_UPDATE drags and the clock beat never arm, so they
    // structurally cannot emit — the flooding case is closed by construction rather than by a
    // rate limit somebody has to maintain.
    //
    // Spending the arm in renderWidgetTrigger's FINALLY is deliberate on both halves: a render
    // that THREW still reports the nodes it reached (which is when a trace is worth most), and
    // it still spends the arm, so a fault cannot leave a widget armed to fire its trace off some
    // later unrelated render.
    //
    // DESIGN-TIME ONLY, gated on CLIENT_KIND === 'editor' — the EXPLICIT ?client= declaration,
    // never IS_DESIGN_TIME (see CLIENT_KIND for why those two are not interchangeable). The gate
    // is on ARMING, which is what makes the production cost real-zero: an OBS source spends one
    // string comparison per activation, the arm set stays empty, and the Evaluator never even
    // allocates a trace collector (see Evaluator.trace and evalNodeOutput).
    //
    // Data-class, best-effort bare send — the same idiom as the two TRIGGER_DIAGNOSTIC senders
    // above, and deliberately NOT the control outbox: a trace queued through a disconnect would
    // flash nodes for an activation that finished minutes ago.
    const _traceArmedWidgets = new Set();   // widgetIds whose next render owes one trace frame
    let   _widgetTraceSeq    = 0;           // monotonic per page, so the editor can drop a stale frame

    function _armWidgetTrace(widgetId) {
        if (CLIENT_KIND !== 'editor' || !widgetId) return;
        _traceArmedWidgets.add(widgetId);
    }

    function sendWidgetNodeTrace(widget, trigger, nodeIds) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        try {
            socket.send(JSON.stringify({
                type:        'DEBUG_WIDGET_NODE',
                layerId,
                widgetId:    (widget && widget.id) || '',
                triggerName: (trigger && trigger.name) || '',
                nodeIds:     nodeIds || [],
                seq:         ++_widgetTraceSeq,
            }));
        } catch (_) { /* best-effort; a trace must never fail a render */ }
    }

    /// V13 H1 — the completion payload a widget's Visual.Complete resolved on its LAST render,
    /// keyed by widget. An ABSENT entry means "this trigger's Visual.Complete has no wired
    /// Payload" (or the graph has no Visual.Complete at all), and that is what makes the wire
    /// field OMITTED rather than empty: the sprint's compatibility gate is that an unwired pin
    /// produces byte-identical wire output, and `"payload": ""` is not the same frame as no
    /// field.
    ///
    /// Per WIDGET rather than one module slot because the animator loop is the one render path
    /// that does NOT take withWidgetLock — a frame for widget B can interleave with A's render,
    /// and a single slot would hand A's waiting script B's payload. handleRunTrigger reads its
    /// own widget's entry into a LOCAL immediately after the trigger render and before the
    /// 2000–60000 ms hold, so the idle revert's onStartup render (a second activation, with its
    /// own Visual.Complete) cannot overwrite the value the triggering activation produced.
    ///
    /// No cleanup pass is needed for a widget deleted by a soft reload: every renderWidgetTrigger
    /// either sets or DELETES the rendered widget's entry, and the only reader runs immediately
    /// after a render of that same widget — so a stale entry can never be read.
    const _widgetCompletionPayload = new Map();   // widgetId → resolved payload string

    async function handleRunTrigger(msg) {
        // Diagnostic ACK on receipt — fired BEFORE any early-return so the Hub
        // can distinguish "browser never saw it" from "browser saw it but
        // bailed". Best-effort; never blocks the actual render.
        sendTriggerReceived(msg);

        // [P1 swarm-audit 2026-05-29] guard layer/layer.widgets — null during
        // bootstrap before LAYER_INIT, or if a stray RUN_TRIGGER arrives early.
        if (!layer || !layer.widgets) {
            sendTriggerDiagnostic(msg, 'no_layer', msg && msg.widgetId);
            sendComplete(msg);
            return;
        }
        const widget = layer.widgets.find(w => w.id === msg.widgetId);
        if (!widget) {
            console.warn('RUN_TRIGGER for unknown widget', msg.widgetId);
            sendTriggerDiagnostic(msg, 'unknown_widget', msg.widgetId);
            sendComplete(msg);
            return;
        }
        // Widget-filter mode (Visualist preview) — only the targeted widget
        // renders. Other widgets' triggers get a noop ack so any waiting Hub
        // script's wait_for_visual still resolves promptly.
        if (!isWidgetVisible(widget)) {
            sendTriggerDiagnostic(msg, 'not_visible', widget.id);
            sendComplete(msg);
            return;
        }
        const trigger = findTrigger(widget, msg.triggerName);
        if (!trigger) {
            console.warn('RUN_TRIGGER for unknown trigger', msg.triggerName, 'on', widget.id);
            sendTriggerDiagnostic(msg, 'unknown_trigger', `widget=${widget.id}`);
            sendComplete(msg);
            return;
        }

        // H62 — serialize all renders for the same widget so a queued RUN_TRIGGER
        // can't begin before the previous one has finished its render+idle-loop pass.
        await withWidgetLock(widget.id, async () => {
            // F5 — capture trigger context BEFORE evaluation so Visual.OnTrigger nodes
            // read the live eventData. RUN_TRIGGER carries `eventData` as an object
            // (Hub sends Dictionary<string,string>; serialised to JSON object client-side).
            triggerContext.layerId       = layerId;
            triggerContext.triggerName   = msg.triggerName || '';
            triggerContext.eventData     = msg.eventData || {};
            try { triggerContext.eventDataJson = JSON.stringify(triggerContext.eventData); }
            catch { triggerContext.eventDataJson = '{}'; }
            triggerContext.timestamp     = Date.now() / 1000;
            // V5 — the trigger starts at t=0, and this widget's activation clock restarts here
            // so the animator's subsequent frames advance from this moment.
            //
            // Resetting timeMs also fixes a latent design-time bug: this handler never touched
            // it, so in the embedded preview a RUN_TRIGGER fired after a scrub inherited the
            // stale scrub cursor and the trigger played from the middle of its timeline. The
            // production path is authoritative — an activation is always t=0.
            triggerContext.timeMs        = 0;
            _stampWidgetActivation(widget.id);
            // A production trigger fire supersedes the editor's pinned cursor: the widget is
            // showing NEW content, so the author's playhead no longer describes what is on
            // screen. Release the design-time latch so this activation animates (on the layer
            // design preview a previously scrubbed widget would otherwise stay frozen).
            _designTimeClockOwners.delete(widget.id);

            // Per-widget dip-to-blank transition duration (0 = instant cut, legacy).
            // Applies to BOTH this swap and the idle revert below — an instant snap
            // back to onStartup is as jarring as the instant swap in. Editor tab
            // switches (SET_ACTIVE_TRIGGER) and scrubbing stay instant by design.
            const transMs = Number(widget.transitionMs) || 0;

            // M58 — sendComplete previously fired unconditionally before the idle-loop
            // pass. Now we only fire VISUAL_COMPLETE after BOTH the trigger render AND
            // the idle-loop onStartup pass have finished, so a Hub script awaiting
            // wait_for_visual sees the same timing as the user actually does on screen.
            // A genuine trigger fire is a new audio activation — one-shot Audio.Play
            // nodes in this trigger play once (the animator re-renders that follow
            // reuse this generation and won't replay them). Scoped to THIS widget so a
            // neighbour mid-hold does not read our bump as its own re-activation.
            bumpAudioActivation(widget.id);
            // V13 A2 — and it is an activation for the trace too, so the render below owes ONE
            // DEBUG_WIDGET_NODE frame. No-op unless this page is an editor surface.
            _armWidgetTrace(widget.id);
            try {
                await renderWithTransition(widget, trigger, transMs);
            } catch (e) {
                console.warn('renderWidgetTrigger failed:', e);
                sendTriggerDiagnostic(msg, 'render_error', (e && e.message) || String(e));
                sendComplete(msg);
                return;
            }
            bumpRenderCount();

            // V13 H1 — capture THIS activation's completion payload now: before the hold, and
            // before the idle revert renders onStartup, whose own Visual.Complete would
            // otherwise overwrite the entry we are about to read. `undefined` (no entry) means
            // the Payload pin was not wired, which omits the field from the ack entirely.
            const completionPayload = _widgetCompletionPayload.get(widget.id);

            if (msg.triggerName !== 'onStartup') {
                // Hold the trigger render on screen for the trigger's
                // configured timeline duration before reverting to onStartup.
                // Without this hold the idle-loop fallback overwrites the
                // trigger's render on the next microtask, so the user sees
                // the trigger flash for microseconds (or not at all).
                //
                // Default when the timeline duration is unset / 0: 2000 ms.
                // Authors can override per trigger via the TimelinePanel
                // duration field in Visualist.
                //
                // D12 doc fix: the effective clamp is [2000, 60000], not [0, 60000]. Anything
                // <= 0 (and anything non-finite) becomes 2000 on the line below, so 2000 is a
                // FLOOR and not just a default — a trigger authored with a 500 ms timeline is
                // still held for 2 s. Hub's WidgetTriggerQueue mirrors this floor when it sizes
                // the per-invocation completion timeout, so the two must not drift.
                let holdMs = Number(trigger && trigger.timeline && trigger.timeline.duration) || 0;
                if (!isFinite(holdMs) || holdMs <= 0) holdMs = 2000;
                if (holdMs > 60000) holdMs = 60000;
                await new Promise(resolve => setTimeout(resolve, holdMs));

                triggerContext.triggerName   = 'onStartup';
                triggerContext.eventData     = {};
                triggerContext.eventDataJson = '{}';
                triggerContext.timestamp     = Date.now() / 1000;
                // The idle revert is its own activation (it re-fires onStartup's one-shot audio
                // via bumpAudioActivation below), so onStartup's animation restarts from t=0 too.
                triggerContext.timeMs        = 0;
                _stampWidgetActivation(widget.id);
                const startup = widget.triggers.find(t => t && t.name === 'onStartup');
                if (startup) {
                    // Reverting to idle is a fresh onStartup activation — let its
                    // audio (if any) fire once, then settle.
                    bumpAudioActivation(widget.id);
                    // V13 A2 — the idle revert is its own activation, so onStartup's own node
                    // walk gets its own trace frame. The author sees what actually ran.
                    _armWidgetTrace(widget.id);
                    try { await renderWithTransition(widget, startup, transMs); bumpRenderCount(); }
                    catch (e) { console.warn('idle-loop onStartup failed:', e); }
                }
            }

            sendComplete(msg, completionPayload);
        });
    }

    /// Acks a RUN_TRIGGER. `payload` is V13 H1's completion payload and is OPTIONAL — every
    /// early-return / error caller omits it, which is correct: those are failure acks whose only
    /// job is to release a waiting script promptly.
    function sendComplete(msg, payload) {
        // Route through the control outbox: a bare socket.send silently dropped
        // VISUAL_COMPLETE acks emitted during a disconnect — the exact cargo the
        // control queue exists to preserve — and Hub-side wait_for_visual then
        // stalled to its full timeout.
        const frame = {
            type: 'VISUAL_COMPLETE',
            layerId,
            widgetId:    msg.widgetId,
            triggerName: msg.triggerName,
            waitId:      msg.waitId || '',
        };
        // V13 H1 — `payload` is APPENDED, and only when the Visual.Complete's Payload pin was
        // actually WIRED. Omitted (not '') otherwise, because §8.1 makes an unwired pin a
        // byte-identical-wire compatibility gate; and appended LAST so the five pre-existing
        // keys keep their existing JSON.stringify order, i.e. an unwired graph emits the exact
        // same bytes it emitted before this sprint.
        if (payload !== undefined && payload !== null) frame.payload = String(payload);
        sendSocket(frame, { control: true });
    }

    // ── Sweep 21 — design-time scrub / play handlers ────────────────────────

    /// SCRUB: render the named widget's named trigger at a single timeMs.
    /// Idempotent — repeated scrubs at the same timeMs produce the same frame.
    async function handleScrub(msg) {
        const widget = (layer && layer.widgets) ? layer.widgets.find(w => w.id === msg.widgetId) : null;
        if (!widget) return;
        const trigger = findTrigger(widget, msg.triggerName);
        if (!trigger) return;

        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = msg.triggerName || '';
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;
        triggerContext.timeMs        = Number(msg.timeMs) || 0;
        // This widget's cursor now belongs to the editor's playhead. On the whole-layer design
        // preview (?client=editor) the production clock is otherwise page-wide, so without this
        // latch the animator loop overwrote the scrubbed timeMs on its next frame and the
        // scrubbed picture lasted about 16 ms. See _designTimeClockOwners.
        //
        // The latch carries the CURSOR, not just the ownership: any other render of this widget
        // (a live patch, a drag, the animator's own frame) re-establishes timeMs from this value
        // instead of inheriting whichever widget the page global was last written for.
        _designTimeClockOwners.set(widget.id, triggerContext.timeMs);

        await withWidgetLock(widget.id, async () => {
            try { await renderWidgetTrigger(widget, trigger); bumpRenderCount(); }
            catch (e) { console.warn('[Visualist] scrub render failed:', e); }
        });
        // #4 — repaint manipulator handles after the re-render wiped the canvas.
        // No-ops when no manipulator is set.
        drawManipulator();
    }

    /// PLAY: start a requestAnimationFrame loop that advances timeMs and
    /// re-renders. Stops on STOP_PLAY or when timeMs reaches durationMs (with
    /// optional looping).
    let _playState = null;  // { widgetId, triggerName, durationMs, loop, startWall, startMs, raf }
    function handlePlay(msg) {
        handleStopPlay();  // make Play idempotent — cancel any existing loop first
        const widget = (layer && layer.widgets) ? layer.widgets.find(w => w.id === msg.widgetId) : null;
        if (!widget) return;
        const trigger = findTrigger(widget, msg.triggerName);
        if (!trigger) return;

        // #6 — drop this widget's animator slot before starting the design-time Play loop.
        // Otherwise the global animator loop keeps re-rendering it in parallel with the Play
        // tick, and the two fight over the canvas so the scrubbed/played GIF frame flickers
        // against the free-running one. handlePlay owns timeMs while it runs (see
        // _productionClockOwnsTime, which suppresses the production clock for the whole page
        // while a Play session exists). The loop ALSO skips PLAY-owned widgets, so this is
        // belt-and-braces — but it is the documented #6 behaviour and it is what makes
        // handleStopPlay's "hold the last played frame" work.
        _widgetAnimators.delete(widget.id);
        // Latch design-time ownership for this widget too, with the cursor the session starts
        // from. Redundant while the session runs (a live _playState suppresses the production
        // clock page-wide), but it keeps ONE record of "a design-time transport owns this cursor,
        // and this is its value" for BOTH transports, released through the one path —
        // handleStopPlay — instead of scrub and play each having their own mechanism. The tick
        // below re-stores it on every frame so the pinned value never lags the picture.
        // ★ Seed from THIS widget's own design-time cursor, never from the page-global
        // triggerContext.timeMs. Before V5 those were the same thing, because SCRUB and
        // PLAY were the only writers of that global. V5 made the production clock write
        // it too — on the whole-layer preview (?client=editor, which is NOT widget-
        // filtered) _productionClockOwnsTime() is true, so the animator tick, the
        // consumer pass and renderAll all rewrite the global with whatever widget they
        // last served, i.e. milliseconds since page load. Pressing ▶ then seeded
        // startMs with tens of seconds, the first tick satisfied
        // `timeMs >= durationMs` for any normal timeline, and the transport rendered
        // the final keyframe and immediately stopped.
        //
        // The widget's own latch is the correct source: SCRUB sets it, so ▶ after a
        // drag resumes from the playhead, and its absence means "never scrubbed" ⇒
        // start at 0, which is what ▶ from a fresh preview must do.
        const playStartMs = _designTimeClockOwners.has(widget.id)
            ? (_designTimeClockOwners.get(widget.id) || 0)
            : 0;
        _designTimeClockOwners.set(widget.id, playStartMs);

        _playState = {
            widget,
            trigger,
            durationMs: Number(msg.durationMs) || 0,
            loop:       !!msg.loop,
            startWall:  performance.now(),
            startMs:    playStartMs,
            raf:        0,
        };
        if (_playState.durationMs <= 0) { _playState = null; return; }

        const tick = async () => {
            // Capture the session once: STOP_PLAY / SET_ACTIVE_TRIGGER arrive
            // synchronously from onMessage and null the module-global while this
            // tick is suspended inside withWidgetLock — every later read must go
            // through `ps`, and session identity is re-checked after each await.
            const ps = _playState;
            if (!ps) return;
            const elapsed = performance.now() - ps.startWall;
            let timeMs = ps.startMs + elapsed;
            if (timeMs >= ps.durationMs) {
                if (ps.loop) {
                    timeMs = timeMs % ps.durationMs;
                    // Reset start so the modulo stays sensible across long loops.
                    ps.startWall = performance.now();
                    ps.startMs   = timeMs;
                } else {
                    timeMs = ps.durationMs;
                    triggerContext.timeMs = timeMs;
                    // Keep the latch's pinned cursor in step with the frame we are about to paint,
                    // so a render from any OTHER path lands on the same value (see
                    // _designTimeClockOwners / _applyWidgetTimeCursor). This is also the value the
                    // held final frame keeps after the session ends.
                    _designTimeClockOwners.set(ps.widget.id, timeMs);
                    await withWidgetLock(ps.widget.id, async () => {
                        if (_playState !== ps) return; // session ended/replaced while awaiting the lock
                        try { await renderWidgetTrigger(ps.widget, ps.trigger); bumpRenderCount(); }
                        catch (e) { console.warn('[Visualist] play final-frame render failed:', e); }
                    });
                    drawManipulator();  // #4 — repaint handles after the final-frame render
                    if (_playState === ps) handleStopPlay();
                    return;
                }
            }
            triggerContext.timeMs = timeMs;
            _designTimeClockOwners.set(ps.widget.id, timeMs);  // pinned cursor tracks the frame
            await withWidgetLock(ps.widget.id, async () => {
                if (_playState !== ps) return; // session ended/replaced while awaiting the lock
                try { await renderWidgetTrigger(ps.widget, ps.trigger); bumpRenderCount(); }
                catch (e) { console.warn('[Visualist] play frame render failed:', e); }
            });
            drawManipulator();  // #4 — repaint handles after each play frame
            if (_playState === ps) ps.raf = requestAnimationFrame(tick);
        };
        _playState.raf = requestAnimationFrame(tick);
    }

    function handleStopPlay() {
        if (_playState && _playState.raf) {
            try { cancelAnimationFrame(_playState.raf); } catch { }
        }
        // #6 — when the design-time Play loop stops, also drop the played widget's animator
        // slot so it doesn't keep free-running after the transport stopped. Without this,
        // stopping playback leaves the GIF looping on its own clock instead of holding the
        // last played frame.
        if (_playState && _playState.widget) {
            _widgetAnimators.delete(_playState.widget.id);
            // ★ The latch is deliberately KEPT, pinned at the frame the transport stopped on.
            //
            // It used to be released here, on the reasoning that "the transport is done, so the
            // widget rejoins the production clock". That reasoning contradicted the line
            // immediately above it: dropping the animator slot exists precisely so the widget
            // HOLDS the last played frame, and releasing the latch handed the same widget's
            // cursor straight back to the production clock, so the very next production-path
            // render painted it at a page-load-old time — visibly snapping it to its end pose
            // the instant the author pressed stop. Two mechanisms, opposite intents, three lines
            // apart.
            //
            // Holding is also the answer the author's gesture implies: they moved a playhead to
            // a frame and stopped there, so that frame is what the pane should show. The pin is
            // released by the three signals that mean the playhead no longer describes the
            // picture — a production RUN_TRIGGER for this widget, a SET_ACTIVE_TRIGGER tab
            // switch (which re-pins to 0), and softReloadLayer on save.
        }
        _playState = null;
        // A live _playState suppresses the production clock PAGE-WIDE, which on the whole-layer
        // design preview means every OTHER time-only slot settled while the session ran (its
        // cursor could not move, so a further frame was provably identical). Now that the page
        // clock is live again those slots have to be re-armed, or one Play session would leave the
        // rest of the layer frozen until the next genuine activation.
        for (const [, slot] of _widgetAnimators) slot.settledAtExtent = false;
        _ensureAnimatorLoop();
    }

    /// Track C — SET_ACTIVE_TRIGGER: the embedded single-widget preview pins the
    /// trigger the editor is currently editing so the pane shows it (instead of
    /// onStartup) and the timeline transport can scrub/play it. Records the
    /// trigger for subsequent renderAll() passes and renders it at t=0 (idle
    /// start state) immediately. Any in-flight playback is cancelled so the new
    /// trigger isn't fighting the old one's rAF loop.
    function handleSetActiveTrigger(msg) {
        handleStopPlay();
        previewActiveTrigger = msg.triggerName || null;

        const widget = (layer && layer.widgets) ? layer.widgets.find(w => w.id === msg.widgetId) : null;
        if (!widget) return;
        const trigger = findTrigger(widget, msg.triggerName)
                     || (widget.triggers ? widget.triggers.find(t => t && t.name === 'onStartup') : null);
        if (!trigger) return;

        // Render the active trigger's starting frame (timeMs = 0) so the pane
        // reflects the edited trigger's idle state until the user scrubs/plays.
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = trigger.name;
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;
        triggerContext.timeMs        = 0;
        // This handler does NOT take the design-time latch (that would freeze a layer-preview
        // widget's ambient animation on a mere tab switch), but if the widget ALREADY holds one
        // from an earlier scrub, the pinned value has to follow the cursor this render establishes
        // — otherwise every later production-path render of that widget would resurrect the old
        // scrub position over the t=0 frame the author is looking at.
        if (_designTimeClockOwners.has(widget.id)) _designTimeClockOwners.set(widget.id, 0);

        // Switching the edited trigger tab is a genuine activation — its one-shot
        // audio plays once; subsequent scrub/play frames reuse this generation.
        // The activation clock restarts with it, the third and last stamp site.
        bumpAudioActivation(widget.id);
        _stampWidgetActivation(widget.id);
        withWidgetLock(widget.id, async () => {
            // V13 A2 — third and last arm site, matching the three genuine activations exactly.
            // Switching the edited trigger tab is precisely when the author wants to see which
            // nodes the newly-selected trigger runs; the scrub / play frames that follow do not
            // re-arm.
            //
            // ★ INSIDE the lock body, not before it, and that is a correctness requirement rather
            // than tidiness. withWidgetLock AWAITS any previous holder, and this call is not
            // awaited, so an arm raised outside could wait many ticks for the render it belongs
            // to — while the global animator loop deliberately does NOT take withWidgetLock, so an
            // animator frame for this same widget can enter renderWidgetTrigger, see the arm and
            // SPEND it first. The author would then get a flash for a frame they never asked for
            // and none for the tab switch they did. Raised here, the arm and the render it is for
            // are the same critical section, exactly like the other two sites in handleRunTrigger.
            _armWidgetTrace(widget.id);
            try { await renderWidgetTrigger(widget, trigger); bumpRenderCount(); }
            catch (e) { console.warn('[Visualist] SET_ACTIVE_TRIGGER render failed:', e); }
        }).then(() => drawManipulator());  // #4 — repaint handles after the re-render
    }

    // ── Phase 9 (a) — render-rate reporting ─────────────────────────────────
    // The Hub status bar exposes a per-layer "FPS" indicator so the streamer
    // can see at a glance whether each browser-source layer is ticking. This
    // is renders-per-second rather than display-frame-rate (the underlying
    // browser tab still runs at the display refresh). Bumped from the
    // message-driven render paths and from the shared consumer pass; flushed
    // once per second and reset.
    //
    // The global animator loop deliberately does NOT bump it — it never did, and
    // making it do so would change what the badge means from "how often is Hub
    // data reaching this layer" (its diagnostic purpose: a layer reading 0 is a
    // layer that is not being fed) to "is a GIF playing", which is always 60.
    let _renderCount = 0;
    function bumpRenderCount() { _renderCount++; }
    // QC28-02 — capture the FPS interval handle so we can clear it when the
    // OBS browser source / WebView2 page becomes hidden (scene cut). Manifesto
    // §4.10 expects "inactive layers cost zero"; without the visibilitychange
    // pause, the heartbeat keeps ticking even when no one is watching.
    let _fpsTimer = null;
    function _startFpsTimer() {
        if (_fpsTimer !== null) return;
        _fpsTimer = setInterval(() => {
            if (!socket || socket.readyState !== WebSocket.OPEN) { _renderCount = 0; return; }
            try { socket.send(JSON.stringify({ type: 'FPS', fps: _renderCount })); }
            catch (e) { /* tolerate transient send failures */ }
            _renderCount = 0;
        }, 1000);
    }
    function _stopFpsTimer() {
        if (_fpsTimer === null) return;
        try { clearInterval(_fpsTimer); } catch { /* ignore */ }
        _fpsTimer = null;
    }
    _startFpsTimer();

    // Clock heartbeat — a 1 Hz re-render tick for browser-autonomous Clock.Now widgets. A
    // Hub-backed timer rides the Overlay Live Channel and is repainted by the patch that carries
    // its new value; a wall clock has no producer at all (evalClockNow reads the OBS machine's
    // own Date.now()), so nothing would ever drive a per-second frame for it. Hence its own
    // selector — it cannot ride the key-narrowed live pass, because it has no key.
    //
    // It no longer has a timer of its own: V5 folded the beat into the ONE global animator loop
    // (_animatorTick / CLOCK_BEAT_INTERVAL_MS / _clockWidgetsPresent, decision record D10). The
    // old setInterval fired independently of the animator, so its consumer pass could overwrite
    // the shared triggerContext singleton in the middle of an animator render that was awaiting
    // an image decode. Sharing the tick makes the two strictly ordered. When the animator has no
    // frame work the loop schedules itself with a timeout sized to the next beat, so a Clock.Now
    // widget on a static layer still costs one wakeup a second, exactly as the interval did.
    //
    // The shared pass early-returns for layers with no Clock.Now widget, so the steady-state cost
    // is one memoized scan. Paused on visibilitychange together with the FPS timer, via
    // pauseAllAnimations, so a hidden OBS scene costs zero.

    // QC28-02 — single entry points to pause/resume the global animator loop (and with it the
    // clock beat). The visibilitychange handler calls these together with the FPS interval so an
    // OBS scene-cut (page hidden) fully quiesces the compositor instead of continuing to burn
    // GPU at full rAF rate.
    let _animationsPaused = false;
    // _nowMs() at the moment the page went hidden, or 0 when visible. The activation clocks are
    // FROZEN across the hidden interval by shifting every stamp forward by this much on resume —
    // see resumeAllAnimations.
    let _animationsPausedAtMs = 0;
    function pauseAllAnimations() {
        if (_animationsPaused) return;
        _animationsPaused = true;
        _animationsPausedAtMs = _nowMs();
        _stopAnimatorLoop();
        // Stop the design-time Play loop too — same rationale: hidden page,
        // zero work.
        if (_playState && _playState.raf) {
            try { cancelAnimationFrame(_playState.raf); } catch { }
            _playState.raf = 0;
        }
    }
    function resumeAllAnimations() {
        if (!_animationsPaused) return;
        _animationsPaused = false;
        // Drop slots whose widget is gone and clear the breaker on the rest — being hidden is
        // not a sticky failure.
        //
        // This used to also fire a burst of concurrent per-widget re-renders, so that each
        // widget's eval pass re-ran requestWidgetAnimator() and an un-animated one got dropped
        // by promoteWidgetAnimator. That burst is now redundant AND harmful: the restarted loop
        // re-renders every registered slot on its very next frame, through the same eval path,
        // with the same demotion rule — and it does so serially, whereas N fire-and-forget
        // renders into one shared canvas with one shared triggerContext is the exact
        // interleaving this sprint removed.
        for (const [widgetId, slot] of Array.from(_widgetAnimators.entries())) {
            const stillPresent = layer && Array.isArray(layer.widgets)
                && layer.widgets.some(x => x && x.id === widgetId);
            if (!stillPresent) { _widgetAnimators.delete(widgetId); continue; }
            slot._consecutiveFailures = 0;
            slot._suspendLogged       = false;
            slot.suspended            = false;
        }
        // _clockBeatLastMs is deliberately NOT re-anchored: a wall clock that has been hidden
        // for minutes is showing a stale time, so the first resumed tick SHOULD beat immediately.
        //
        // The per-widget activation clocks are FROZEN across the hidden interval instead — every
        // stamp is shifted forward by however long the page was hidden, so a widget resumes at
        // exactly the elapsed time it was suspended at.
        //
        // Both of the alternatives are wrong, and one of them was what shipped:
        //   • leave the stamps alone (the previous behaviour). _nowMs() is performance.now(),
        //     which keeps advancing while the page is hidden, so a 3 s OBS scene cut
        //     FAST-FORWARDED every in-flight animation by 3 s. A 2000 ms intro that had played
        //     200 ms resumed past its own extent, settled on its last keyframe, and — because the
        //     settle mark only clears on a genuine activation — could never play again for that
        //     socket's life. Every alert that fired near a scene cut was silently lost.
        //   • re-anchor to now. That restarts the track, i.e. it REPLAYS an alert's intro on every
        //     scene return with no alert behind it. That is the property the old comment here was
        //     defending, and freezing preserves it: a scene cut is not an activation.
        // Freezing keeps both: no replay, and no skipped remainder.
        //
        // ACCEPTED CONSEQUENCE, stated plainly: a hold is paused, not cancelled. A widget hidden
        // 30 s into a 2 s intro still has ~1.8 s of that intro left to play when the scene comes
        // back, so the viewer sees the tail of an animation for an event that happened 30 s ago.
        // That is the lesser of the three — one short visible tail, versus either a permanently
        // dead widget or a phantom replay.
        if (_animationsPausedAtMs) {
            const hiddenMs = _nowMs() - _animationsPausedAtMs;
            if (hiddenMs > 0) {
                const nowMs = _nowMs();
                for (const [id, start] of Array.from(_widgetActivationStart.entries())) {
                    // Never shift a stamp past NOW: a RUN_TRIGGER that arrived WHILE the page was
                    // hidden stamped mid-interval, and adding the whole hidden duration would put
                    // its start in the future — _widgetTimeMs clamps negatives to 0, so the widget
                    // would sit frozen at t=0 for the residual instead of starting at once.
                    const shifted = start + hiddenMs;
                    _widgetActivationStart.set(id, shifted > nowMs ? nowMs : shifted);
                }
            }
            _animationsPausedAtMs = 0;
        }
        _ensureAnimatorLoop();
        // Resume the design-time Play loop if one was running.
        if (_playState && _playState.raf === 0) {
            // Re-anchor wall time so resume doesn't fast-forward the playhead
            // by the entire hidden interval.
            _playState.startWall = performance.now();
            _playState.raf = requestAnimationFrame(function _resumeTick() {
                // handlePlay's `tick` is closed over the prior _playState; we
                // can't re-enter it directly. Cheapest correct path: stop and
                // restart the play loop fresh from current state.
                const ps = _playState;
                if (!ps) return;
                handleStopPlay();
                handlePlay({ widgetId: ps.widget.id, triggerName: ps.trigger.name,
                             durationMs: ps.durationMs, loop: ps.loop });
            });
        }
    }

    if (typeof document !== 'undefined' && document.addEventListener) {
        document.addEventListener('visibilitychange', () => {
            if (document.hidden) {
                _stopFpsTimer();
                pauseAllAnimations();   // also stops the clock beat — it rides the same loop
            } else {
                _startFpsTimer();
                resumeAllAnimations();
            }
        });
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    /// The trigger name a WHOLE-LAYER paint renders.
    ///
    /// Track C — in single-widget preview mode the editor can pin an "active trigger" (the
    /// trigger tab being edited) via SET_ACTIVE_TRIGGER. When present we render THAT trigger (at
    /// the current timeMs) instead of the onStartup idle state so keyframing is visible. Bare
    /// layer / OBS renders and the initial paint (previewActiveTrigger === null) keep onStartup.
    function defaultRenderTriggerName() {
        return (widgetFilterId && previewActiveTrigger) ? previewActiveTrigger : 'onStartup';
    }

    /// The trigger a whole-layer paint renders FOR ONE WIDGET: the pinned name when this widget
    /// has it, else its onStartup idle. `name` lets renderAll pass the value it resolved once for
    /// the whole pass, so a SET_ACTIVE_TRIGGER arriving mid-pass cannot make two widgets in the
    /// same paint disagree with triggerContext.triggerName.
    ///
    /// Single-sourced because two callers must agree on the answer: renderAll (which paints it)
    /// and _seedWidgetAnimator (which registers an animator slot for it before renderAll's
    /// promotions have landed). If they disagreed, the seeded slot would animate a different
    /// trigger than the one on screen.
    ///
    /// The onStartup fallback skips null / nameless elements for the same reason findTrigger's two
    /// passes do: _seedWidgetAnimator calls this for EVERY widget on the layer, so a corrupt
    /// element here throws on a path that has no per-widget try/catch (see widgetConsumesTime).
    function defaultRenderTrigger(widget, name) {
        if (!widget || !widget.triggers) return null;
        return findTrigger(widget, name || defaultRenderTriggerName())
            || widget.triggers.find(t => t && t.name === 'onStartup')
            || null;
    }

    /// Z-ORDER FIX — paint order for a whole-layer repaint.
    ///
    /// The full-paint loops used to walk `layer.widgets` in serialized array order. Nothing in
    /// the editor's restack commands reorders that array — Bring-Forward / Send-Back only assign
    /// LayerWidget.ZIndex — so restacking a widget in Visualist never changed what OBS drew, and
    /// two overlapping widgets stacked by whichever happened to be saved last.
    ///
    /// Sorts a COPY: layer.widgets is indexed into by other consumers, so reordering it in place
    /// would move widgets out from under them. Array#sort is specified stable, which makes equal
    /// zIndex keep serialized order — the same tiebreak the editor's OrderBy(ZIndex).ThenBy(index)
    /// uses. Missing/garbage zIndex reads as 0 so a pre-zIndex .phxlayer paints as authored.
    function widgetsInPaintOrder(widgets) {
        if (!Array.isArray(widgets)) return [];
        return widgets.slice().sort((a, b) => {
            const az = (a && Number.isFinite(Number(a.zIndex))) ? Number(a.zIndex) : 0;
            const bz = (b && Number.isFinite(Number(b.zIndex))) ? Number(b.zIndex) : 0;
            return az - bz;
        });
    }

    async function renderAll() {
        // Guard the whole pass — a malformed/half-loaded layer (layer or
        // layer.widgets null/undefined) would otherwise throw TypeError on the
        // for-of below and abort the entire first paint. Symmetric with the
        // guard _renderConsumerPass already has.
        if (!layer || !layer.widgets) return;
        // Resolved ONCE for the pass — see defaultRenderTriggerName (Track C).
        const renderTriggerName = defaultRenderTriggerName();
        // F5 — seed trigger context for the pass so Visual.OnStartup / Visual.OnTrigger
        // nodes have a populated LayerId / Timestamp before evaluation.
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = renderTriggerName;
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;

        ctx.clearRect(0, 0, logicalW, logicalH);
        paintBackdrop({ x: 0, y: 0, width: logicalW, height: logicalH });
        // Z-ORDER FIX — paint back-to-front by zIndex, not by array position. See
        // widgetsInPaintOrder above.
        for (const widget of widgetsInPaintOrder(layer.widgets)) {
            if (!isWidgetVisible(widget)) continue;
            // [P1 swarm-audit 2026-05-29] guard widget.triggers — null/undefined
            // on a malformed widget; skip it rather than throwing on .find.
            if (!widget.triggers) continue;
            // Honor the active trigger when pinned (single-widget preview); fall
            // back to onStartup if the named trigger isn't present on this widget.
            const trig = defaultRenderTrigger(widget, renderTriggerName);
            if (!trig) continue;
            // QC28-01 — symmetric with _renderConsumerPass / handleRunTrigger which
            // both wrap per-widget renders. Without this, a single bad widget's throw
            // aborts the full first-paint of the layer (the for-loop unwinds before
            // subsequent widgets get a chance). Log + continue so the rest of the
            // layer still paints; the broken widget will just show as blank/diagnostic.
            // V5 — each widget renders at ITS OWN activation clock, same rule as the animator
            // loop and the consumer pass. On the bootstrap paint every widget lazily stamps
            // here, so a keyframed onStartup track starts at t=0 rather than inheriting the
            // clock of whichever widget was painted before it. Per-WIDGET ownership: on the
            // whole-layer design preview a scrubbed widget keeps the author's pinned cursor
            // through a full repaint (the helper re-writes the pinned value, so the repaint can't
            // silently adopt a neighbour's clock) while its neighbours keep animating.
            _applyWidgetTimeCursor(widget.id);
            try {
                await renderWidgetTrigger(widget, trig);
                bumpRenderCount();
            } catch (e) {
                console.error('[compositor] renderAll: widget render failed', widget && widget.id, e);
            }
        }
        // Drop DOM overlays for widgets that are no longer in the layer (e.g. a widget
        // deleted in the editor between renders). Per-widget/trigger churn is handled by
        // reconcileWidgetOverlays inside renderWidgetTrigger; this catches removed widgets.
        const _liveWidgetIds = new Set((layer.widgets || []).map(w => w && w.id));
        try { sweepWebOverlays(_liveWidgetIds); }
        catch (e) { console.warn('[Visualist] WebOverlay sweep failed:', e); }
        // V15 — the same sweep for iframe players, and it matters more here than for the
        // WebOverlay track: an orphaned player entry keeps a live iframe and its watchdog
        // timers, not just a stale <div>.
        try { sweepPlayerEmbeds(_liveWidgetIds); }
        catch (e) { console.warn('[Visualist] Player.Embed sweep failed:', e); }
    }

    /// Targeted WIDGET_UPDATE repaint — the smooth-drag fast path. Re-renders ONLY
    /// the moved widget instead of re-evaluating EVERY widget's graph (what a full
    /// renderAll does on each drag frame). Returns true when it handled the repaint,
    /// false to tell the caller "fall back to renderAll" (today's exact behaviour).
    ///
    /// Safe to targeted-repaint ONLY when the widget's vacated∪new region overlaps
    /// no other widget: all widgets share one <canvas>, and renderWidgetTrigger's
    /// clearRect ignores the clip path, so a partial repaint of an overlapping
    /// neighbour would erase its pixels. Any overlap (or filter/degenerate case)
    /// returns false so the correct full paint runs. The moved widget itself renders
    /// through the identical renderWidgetTrigger path (same centred-extent/clip
    /// contract), so a non-overlapping repaint is pixel-identical to renderAll's.
    async function patchWidgetUpdate(movedWidget, oldRect) {
        if (!layer || !layer.widgets) return false;
        // Single-widget preview fills the whole stage and renders exactly one
        // widget — renderAll is already cheap and the dirty-rect math doesn't apply.
        if (widgetFilterId) return false;
        const newRect = movedWidget && movedWidget.rect;
        if (!newRect || !(newRect.width > 0) || !(newRect.height > 0)) return false;
        const dirty = unionRect(oldRect || newRect, newRect);

        // Overlap guard — defer to the full paint if the vacated∪new region touches
        // any other widget.
        for (const other of layer.widgets) {
            if (!other || other === movedWidget || !other.rect) continue;
            if (rectsIntersect(dirty, other.rect)) return false;
        }

        // Seed the pass context exactly as renderAll does so Visual.OnStartup /
        // keyframe sampling see the same LayerId / timestamp. Layer mode always
        // renders the onStartup idle (we already returned false for filter mode).
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = 'onStartup';
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;

        // Clear the vacated∪new region (+ backdrop) then repaint just this widget.
        ctx.clearRect(dirty.x, dirty.y, dirty.width, dirty.height);
        paintBackdrop(dirty);
        if (!isWidgetVisible(movedWidget) || !movedWidget.triggers) return true; // hidden → cleared region is the result
        const trig = findTrigger(movedWidget, 'onStartup')
                  || movedWidget.triggers.find(t => t && t.name === 'onStartup');
        if (!trig) return true;
        // V5 — this widget's own activation clock, same rule as renderAll (this IS renderAll's
        // fast path). Without it a drag frame would repaint the moved widget at the clock of
        // whichever widget the animator loop rendered last — and for a SCRUBBED widget being
        // dragged, at a neighbour's clock rather than the author's pinned cursor.
        _applyWidgetTimeCursor(movedWidget.id);
        await renderWidgetTrigger(movedWidget, trig);
        bumpRenderCount();
        return true;
    }

    /// WIDGET_UPDATE render dispatch. A pure rect change (the common drag/resize)
    /// tries the targeted fast path; a zIndex change reorders the whole paint stack
    /// so it always takes the full renderAll. Falls back to renderAll whenever the
    /// targeted path can't safely repaint in place.
    async function applyWidgetUpdateRender(movedWidget, oldRect, rectChanged, zChanged) {
        if (rectChanged && !zChanged) {
            let handled = false;
            try { handled = await patchWidgetUpdate(movedWidget, oldRect); }
            catch (e) {
                console.warn('[Visualist] targeted WIDGET_UPDATE repaint failed, full render:', e);
                handled = false;
            }
            if (handled) return;
        }
        await renderAll();
    }

    /// Post a RENDER_ACK back to the WinUI capture host (WidgetCanvasPreviewer) once
    /// a WIDGET_UPDATE has re-rendered AND been presented, so the host can grab the
    /// fresh frame deterministically instead of guessing with staggered timers. The
    /// double-rAF defers the ack until the browser has composited the new frame — a
    /// capture fired the instant JS finished drawing could still grab the pre-present
    /// frame (exactly why the C# side used the 40/120 ms stagger). No-op outside a
    /// WebView2 host (OBS / bare browser have no chrome.webview).
    function postRenderAck(widgetId) {
        try {
            if (typeof chrome === 'undefined' || !chrome.webview || !chrome.webview.postMessage) return;
            requestAnimationFrame(() => requestAnimationFrame(() => {
                try { chrome.webview.postMessage({ type: 'RENDER_ACK', widgetId: widgetId }); } catch { }
            }));
        } catch { }
    }

    /// Soft LAYER_RELOADED — re-fetch the layer JSON and repaint in place instead of
    /// a hard window.location.reload(). Kills the OBS/preview flash on every save and
    /// keeps the WS, the decoded-image cache, the translation cache and the Overlay Live
    /// Channel's entries alive across the reload. Any failure throws so the caller falls
    /// back to the hard reload (today's exact behaviour).
    async function softReloadLayer() {
        // Cache-bust so CEF can't hand back a stale layer body across the reload.
        const resp = await fetch(`/api/layer/${encodeURIComponent(layerId)}?t=${Date.now()}`);
        if (!resp.ok) throw new Error('layer fetch ' + resp.status);
        const newLayer = await resp.json();
        if (!newLayer || !Array.isArray(newLayer.widgets)) throw new Error('malformed layer json');

        // Graphs may have changed — drop the per-widget/trigger memo (its Display /
        // consumer / sink scans are cached immutable-until-reload).
        _triggerMeta.clear();

        // Tear down EVERY animator slot before the swap so no ghost animator keeps painting an
        // old (possibly removed) widget object; renderAll re-promotes the ones still animated.
        // Simpler and safer than a per-widget diff. The render mutex map is left intact so an
        // in-flight render serialises with the fresh one rather than racing it. Media / image /
        // translation caches and the live channel's entries deliberately survive — the win over
        // the hard reload. Activation stamps survive too (a save is not an activation, and
        // restarting every animation from t=0 on each keystroke-driven save would be worse);
        // _refreshAnimatorDemand below prunes the ones whose widget is gone.
        _stopAnimatorLoop();
        _widgetAnimators.clear();
        _animatorResumeId = null;   // the round-robin cursor named a slot that no longer exists
        // Also forget any in-flight render. A wedged widget (a remote source that accepted the
        // connection and then stalled) would otherwise stay blocked from ever animating again
        // for the rest of the page's life; a .phxlayer save is the author's "start over", and
        // it is exactly the recovery the N per-widget loops had.
        _animatorInFlight.clear();
        // Same argument for the animator's stall report: a save is the author's "start over", so
        // let the next wedge be reported instead of staying silent because an earlier version of
        // the graph already used up the one-shot.
        _animatorStallLatch.warned = false;
        // And the same argument for the design-time cursor latches. A pinned playhead describes a
        // TIMELINE the save may have just re-authored (keyframes moved, the duration changed, the
        // trigger renamed), so holding a widget at a stale cursor across a reload would pin it to a
        // position that no longer means anything — and while latched it is refused an animator
        // seed, so it would also sit out the fresh renderAll's animation. A save is the author's
        // "start over"; the next SCRUB / PLAY re-takes ownership immediately.
        _designTimeClockOwners.clear();

        const resolutionChanged = !layer || !layer.resolution || !newLayer.resolution
            || layer.resolution.width  !== newLayer.resolution.width
            || layer.resolution.height !== newLayer.resolution.height;

        layer = newLayer;

        if (resolutionChanged) {
            applyResolution();
            try { syncManipulatorOverlaySize(); } catch { }
        }

        await renderAll();
        drawManipulator();

        // The reloaded graphs may have gained or lost a Clock.Now widget, and widgets may have
        // been deleted. Re-derive the clock demand and prune the activation stamps.
        _refreshAnimatorDemand();

        // The reloaded graphs may read a different live-key set, so re-derive and re-announce.
        // FORCED: a LAYER_RELOADED is also what a delete+recreate looks like from here, and
        // OnLayerRemoved has by then cleared this layer's subscription Hub-side even though our
        // socket never closed. An unforced call would no-op on the unchanged-key-set guard for
        // the common save (rect nudge, colour tweak) and leave the overlay silently starved of
        // live data for the rest of this socket's life. The cost of forcing is one extra
        // whole-store LIVE_SNAPSHOT per .phxlayer save — a design-time-only event.
        sendLiveHello({ force: true });
    }

    // ── The ONE consumer re-render pass ─────────────────────────────────────
    //
    // Five near-identical passes used to live here — renderCaptionConsumers,
    // renderTimerConsumers, renderClockConsumers, renderLoyaltyConsumers and
    // renderCounterConsumers — differing only in the one line that decided whether a widget
    // was interesting. Four of them existed because each data family had its own push frame;
    // those frames are gone, so the four selectors collapsed into ONE key-level selector
    // (_widgetLiveKeys). The clock survives as a selector, not as a pass.
    //
    // What the shared body preserves, unchanged from the clones: the onStartup trigger
    // context, the serial for-loop, the per-widget withWidgetLock so an in-flight RUN_TRIGGER
    // render is never clobbered, the per-widget try/catch so one bad widget cannot abort the
    // rest, and bumpRenderCount() inside the try.
    //
    // What it adds: the S7 guarantee, now universal. The clones wrote the shared
    // triggerContext singleton BEFORE discovering they had nothing to do — and
    // renderWidgetTrigger awaits image decodes, so an alert render in flight would resume
    // against a blanked context and paint with no donor name and no amount. The pre-scan below
    // makes "no work" cost zero writes for every caller, not just the live one.
    //
    //   _liveRenderPending  — a pass is running; further calls coalesce instead of racing it.
    //   _liveRenderDirty    — keys accumulated during the running pass (null when none).
    //   _liveRenderDirtyAll — a LIVE_SNAPSHOT landed mid-pass, so the follow-up round has to
    //                         re-render everything and the per-key set is moot.
    //
    // The latch guards the LIVE path only. The clock heartbeat deliberately does not take it:
    // it fires at a fixed 1 Hz that no knob raises, its widgets are disjoint from the live ones
    // in every realistic layer, and withWidgetLock already serialises any widget the two passes
    // did share. Giving it the latch would let a slow live pass swallow clock ticks.
    let _liveRenderPending  = false;
    let _liveRenderDirty    = null;
    let _liveRenderDirtyAll = false;

    // One-shot "gave up waiting on a widget" latches, one per PASS FAMILY rather than one for
    // the page. They used to be a single boolean, so whichever pass stalled first silenced the
    // report for the other one forever — and the two mean very different things: a stalled live
    // pass is a wedged data-driven widget, a stalled animator frame is the thing that just cost
    // every OTHER widget on the layer its animation (the loop is serial). Diagnosing the second
    // must not depend on the first never having happened.
    const _liveStallLatch     = { warned: false };
    const _animatorStallLatch = { warned: false };

    // Ceiling on how long ONE widget may hold up a consumer pass. The latch above is
    // page-lifetime and only its own `finally` clears it, so an await that never settles would
    // strand it true and silently swallow every future patch for the rest of the page's life —
    // converting one wedged widget into a dead channel. That is reachable: loadImage and the
    // blur rasteriser wire only onload/onerror, so a remote host that accepts the connection
    // and then stalls leaves the promise pending forever. The latch is required (unguarded
    // stacking is worse), so it gets a bound instead. The clock pass shares the bound even
    // though it holds no latch — a wedged widget must not silently stop the wall clock either.
    //
    // The global animator loop (V5) shares it for the strongest version of the same reason: it
    // is serial, so ONE stalled widget would otherwise stop every other widget's animation AND
    // the clock beat, page-permanently. Its per-slot `inFlight` flag is what keeps the cost of a
    // stalled widget at one slow frame rather than one slow frame per frame, and the loop's frame
    // budget (ANIMATOR_FRAME_BUDGET_MS) is what keeps N stalled widgets from costing N × this
    // ceiling on a SINGLE tick: the first over-budget render ends the tick, so the rest of the
    // registry is served on following ticks instead of queueing behind every stall in turn.
    //
    // Racing does NOT risk interleaved renders of the same widget: withWidgetLock chains onto the
    // prior promise, so a later pass still queues behind the stalled render. Losing the race only
    // lets the pass move on to the NEXT widget, which is what completion would have done anyway.
    const LIVE_PASS_WIDGET_TIMEOUT_MS = 5000;

    /// Awaits `work`, giving up after LIVE_PASS_WIDGET_TIMEOUT_MS. Warns once per page PER
    /// LATCH (`latch`, default the live pass's) so a chronically stalling widget is diagnosable
    /// without spamming the console at pump cadence — and so one pass family's stall cannot
    /// swallow another's report. The timer is always cleared, so a fast render leaves nothing
    /// pending behind it.
    function _raceWidgetRender(work, widgetId, label, latch) {
        const warnLatch = latch || _liveStallLatch;
        let timer = 0;
        const bail = new Promise(resolve => {
            timer = setTimeout(() => {
                if (!warnLatch.warned) {
                    warnLatch.warned = true;
                    console.warn(
                        `[Visualist] ${label} pass gave up waiting on widget '${widgetId}' after ` +
                        `${LIVE_PASS_WIDGET_TIMEOUT_MS} ms — its render is still in flight. The ` +
                        `overlay continues; a stalled remote image/video source is the usual cause.`);
                }
                resolve();
            }, LIVE_PASS_WIDGET_TIMEOUT_MS);
        });
        return Promise.race([work, bail]).finally(() => { try { clearTimeout(timer); } catch { } });
    }

    /// THE consumer pass — one body, shared by the live channel and the clock heartbeat.
    ///
    /// `selects(widget)` decides whether a widget is interesting; everything else is the
    /// invariant set the five deleted clones all carried. `label` only names the pass in a
    /// diagnostic, so a stall report says which caller was starved.
    ///
    /// The pre-scan is load-bearing, not an optimisation: the shared triggerContext singleton
    /// must not be touched by a pass that turns out to have nothing to render, because
    /// renderWidgetTrigger awaits image decodes and an alert render already in flight would
    /// resume against the blanked context and paint with no donor name and no amount.
    async function _renderConsumerPass(selects, label) {
        if (!layer || !layer.widgets) return;

        // Cheap because both selectors are memoized (getTriggerMeta / _widgetLiveKeys); the
        // loop below re-asks rather than materialising a list, so a widget hidden between the
        // two passes is still skipped by the live isWidgetVisible check.
        //
        // The null-widget guard is not paranoia: isWidgetVisible returns TRUE for a null widget
        // in layer mode (its test is `!widgetFilterId || …`), so a malformed .phxlayer with a
        // hole in its widgets array would reach the selector and throw on `widget.id`. renderAll
        // and sweepWebOverlays already defend the same way.
        let any = false;
        for (const widget of layer.widgets) {
            if (widget && isWidgetVisible(widget) && selects(widget)) { any = true; break; }
        }
        if (!any) return;

        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = 'onStartup';
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;

        // Z-ORDER FIX — this pass repaints a SUBSET onto the same shared canvas, so it has to
        // stack the same way renderAll does or a live patch would contradict the full paint that
        // preceded it. (The pre-scan above stays in array order: it only answers "is there any
        // work?", where order is meaningless.)
        for (const widget of widgetsInPaintOrder(layer.widgets)) {
            if (!widget || !isWidgetVisible(widget)) continue;
            if (!selects(widget)) continue;
            const trig = (widget.triggers || []).find(t => t && t.name === 'onStartup');
            if (!trig) continue;
            // V5 — the same per-widget activation clock the animator loop writes. A keyframed
            // widget can ALSO be a live-key or Clock.Now consumer, so it gets re-rendered here
            // too; without this write it would sample at whatever timeMs the previously rendered
            // widget left behind, which is a different widget's clock. A design-time-owned
            // (scrubbed) widget keeps its pinned cursor through a live patch / clock beat —
            // written, not merely left standing, since the value in the singleton right now
            // belongs to whichever widget this pass rendered previously.
            _applyWidgetTimeCursor(widget.id);
            // Bounded — see LIVE_PASS_WIDGET_TIMEOUT_MS. A never-settling render must not strand
            // the pending latch and take the whole channel down with it.
            await _raceWidgetRender(withWidgetLock(widget.id, async () => {
                try { await renderWidgetTrigger(widget, trig); bumpRenderCount(); }
                catch (e) { console.warn(`${label}-consumer rerender failed:`, e); }
            }), widget.id, label);
        }
    }

    /// Re-renders the widgets bound to live keys. `changedKeys` is the Set of literal keys a
    /// LIVE_PATCH carried, or null for "everything" (LIVE_SNAPSHOT). This is the channel's
    /// entry point: it owns the coalescing latch and the drain loop, and delegates the actual
    /// walk to _renderConsumerPass.
    async function renderLiveConsumers(changedKeys) {
        // Provably inert for a layer with no live bindings — and this bail is why. A
        // LIVE_SNAPSHOT arrives on EVERY socket open for EVERY layer, so without it the pass
        // below would overwrite the shared triggerContext singleton (triggerName, eventData,
        // timestamp) on a layer that has nothing to re-render. renderWidgetTrigger awaits image
        // decodes, so an alert render already in flight would resume against the blanked
        // context and paint with no donor name and no amount. Bail BEFORE any write to it.
        // Duplicated at the top of _liveConsumerPass so the drain loop cannot re-enter past it.
        if (_widgetLiveKeys.size === 0) return;

        if (_liveRenderPending) {
            if (changedKeys === null) { _liveRenderDirtyAll = true; _liveRenderDirty = null; }
            else if (!_liveRenderDirtyAll) {
                if (!_liveRenderDirty) _liveRenderDirty = new Set();
                for (const k of changedKeys) _liveRenderDirty.add(k);
            }
            return;
        }

        _liveRenderPending = true;
        try {
            let keys = changedKeys;
            // Drain loop rather than re-entering: however many patches land during a round,
            // they collapse into exactly one follow-up round carrying their union.
            for (;;) {
                await _liveConsumerPass(keys);
                if (_liveRenderDirtyAll)   keys = null;
                else if (_liveRenderDirty) keys = _liveRenderDirty;
                else break;
                _liveRenderDirtyAll = false;
                _liveRenderDirty    = null;
            }
        } finally {
            _liveRenderPending  = false;
            _liveRenderDirtyAll = false;
            _liveRenderDirty    = null;
        }
    }

    /// One live round over the layer: the shared pass, narrowed to the widgets whose subscribed
    /// keys intersect `changedKeys` (null = a snapshot, so every bound widget repaints).
    async function _liveConsumerPass(changedKeys) {
        // Second half of the inert-layer guard (see renderLiveConsumers): the drain loop calls
        // straight back in here, and a soft reload can empty _widgetLiveKeys between rounds. No
        // bindings means nothing to render at all, so bail before even building the selector.
        // _renderConsumerPass's own pre-scan would also catch this, but the explicit check is
        // what makes "a layer with no live bindings is provably inert" readable at a glance.
        if (_widgetLiveKeys.size === 0) return;

        await _renderConsumerPass(widget => {
            const subscribed = _widgetLiveKeys.get(widget.id);
            if (!subscribed || subscribed.length === 0) return false;
            // Patch-scoped round — skip a widget none of whose keys are in the changed set.
            if (changedKeys && !subscribed.some(k => _liveEntryMatchesChanged(k, changedKeys))) return false;
            return true;
        }, 'live');
    }

    /// Renders the widget's trigger graph. Visits Display first (the visual sink),
    /// then visits Visual.Complete if the graph wires one (the completion sink).
    /// Returns when both sinks have settled (or only Display if no completion node).
    async function renderWidgetTrigger(widget, trigger) {
        // Sweep 21 — bind the active timeline so attribute readers can sample
        // animated parameters at triggerContext.timeMs.
        //
        // D12 doc fix: this used to claim the binding is "reset to null after the render". It
        // is NOT — nothing in this file ever assigns activeTimeline = null. It stays bound to
        // the LAST trigger rendered, and the only thing that keeps that harmless is that every
        // render path re-binds it here on entry. Which is precisely why the animator loop is
        // serial: activeTimeline is a module-level singleton read by attrAnimated DURING async
        // evaluation, so two renders in flight at once would sample each other's timeline.
        activeTimeline = trigger.timeline || null;
        // Track E — bind the trigger's master volume (0..1) so evalAudioPlay can
        // scale each Audio.Play node's level by it. Default 1 when the trigger
        // predates the property so existing layers play at their authored volume.
        //
        // WIRE-NAME FIX — the key is lower-case 'volume'. WidgetTrigger.Volume carries
        // [JsonPropertyName("volume")], so the PascalCase read this used to do was permanently
        // undefined on parsed layer JSON and the fader below was a no-op: every trigger played
        // at 1.0 no matter where the mixer sat. The PascalCase arm survives only as a fallback
        // for a hand-written .phxlayer.
        activeTriggerVolume = clamp(Number(trigger.volume ?? trigger.Volume ?? 1), 0, 1);
        const ev = new Evaluator(trigger.graph, widgetRenderRect(widget), widget && widget.id);

        // V13 A2 — collect visited node ids ONLY when this widget owes a trace frame. On a
        // production OBS source the arm set is permanently empty (see _armWidgetTrace's
        // CLIENT_KIND gate), so ev.trace stays null and evalNodeOutput's whole per-node cost is
        // one property read. Captured into a local rather than re-tested later: the arm is
        // cleared in the finally below, and re-reading the set there would make "did I collect?"
        // and "should I send?" two different questions.
        const _traceThisRender = _traceArmedWidgets.has((widget && widget.id) || '');
        if (_traceThisRender) ev.trace = new Set();

        // The two animator request flags are module-level (the Evaluator raises them from free
        // functions, deep inside image/video/particle kernels), so they are SAVED here, cleared,
        // and RESTORED in the finally below. Same save+restore shape ScriptEngine uses for its
        // per-execution state, and it closes two ways the promotion latch could be donated to a
        // widget that asked for nothing:
        //
        //   • a THROWN render. The request is raised at the top of this try and consumed at the
        //     bottom by promoteWidgetAnimator, so anything that threw in between used to leave
        //     the flag standing — and the NEXT widget rendered got promoted on it. An
        //     intermittently-throwing widget re-leaked it every frame.
        //   • an OVERLAPPING render. This loop is not the only render path (see the animator
        //     block comment): an animator frame can interleave with a message-driven render.
        //     Whoever promoted first cleared the other's flag, so the second one demoted a
        //     widget that had raised its request — the animation just stopped. Restoring the
        //     value the interrupted render had makes each render's request its own.
        const _savedAnimatorRequestMedia = _animatorRequestMediaForCurrentRender;
        const _savedAnimatorRequestTime  = _animatorRequestTimeForCurrentRender;
        _animatorRequestMediaForCurrentRender = false;
        _animatorRequestTimeForCurrentRender  = false;

        // This render's position in RENDER ORDER — the second half of making the save/restore
        // above safe, and the thing that stops a stale render from winning. See _renderSeq and
        // promoteWidgetAnimator's stale-promotion rejection for the exact interleave.
        const renderSeq = ++_renderSeq;

        try {
            // L48 — Display sink is now resolved via getTriggerMeta which dedupe-scans the
            // graph and warns when more than one Display node is present (memoized per
            // widget+trigger so the scan only runs on first visit / after layer reload).
            const meta = getTriggerMeta(widget, trigger);

            // V5 — the second half of the production-clock fix, and the half that is easy to
            // miss. The animator request is an OPT-IN LATCH: promoteWidgetAnimator (bottom of
            // this function) keeps a widget in the animator loop only if something during the
            // render raised it, and demotes it to render-once otherwise. Before V5 the only
            // callers were the animated-GIF branches of evalImageLoad / evalImageLoadUrl,
            // Particles.Emit and evalVideoLoad — so a keyframed widget was demoted after its
            // first paint and never re-rendered. Advancing timeMs without this call changes
            // NOTHING on screen: the frame it would have advanced never happens.
            //
            // Routing it through the same latch (rather than promoting directly) is what keeps
            // demotion symmetric: a widget whose author deletes the last keyframe stops raising
            // the flag on its next render and drops out of the loop by the existing rule.
            if (meta.consumesTime) requestWidgetTimeAnimator();

            const display = meta.displayNode;
            // A widget whose only content is a WebOverlay.Custom sink legitimately leaves
            // the auto-injected Display unwired — suppress the "no Image input" hint card
            // in that case so the DOM overlay isn't backed by a diagnostic rectangle.
            // V15 — a Player.Embed widget is in exactly the same position: its content is an
            // iframe on the DOM track and its Display is legitimately unwired, so without
            // counting it here the preset would paint a "no Image input" diagnostic card
            // BEHIND the player on every render.
            const hasOverlaySink = meta.overlaySinks.length > 0 || meta.playerSinks.length > 0;
            if (display) {
                debugLog('renderWidgetTrigger.start', { widgetId: widget.id, trigger: trigger.name });
                // Pre-eval check: does Display even have an inbound Image link? Lets
                // us tell the difference between "evaluator returned null because
                // upstream failed" and "graph has no upstream wired at all", which
                // is the first thing P0 #1's body-preview revealed: most "image
                // not transmitting" reports are unwired graphs.
                const inLink = ev.findLinkTo(display.Id, 'Image');
                const result = await ev.evalImageInto(display);
                // Widget-filter mode renders into the canvas-sized rect (the canvas
                // IS the widget); layer mode renders into the widget's authored rect.
                const renderRect = widgetRenderRect(widget);
                ctx.clearRect(renderRect.x, renderRect.y, renderRect.width, renderRect.height);
                paintBackdrop(renderRect);
                if (result && result.image) {
                    const iw = result.width  || result.image.width;
                    const ih = result.height || result.image.height;
                    const rw = renderRect.width;
                    const rh = renderRect.height;
                    // A/C — the widget IS a fixed-resolution canvas (renderRect == the
                    // widget's authored W×H). Draw the composed content at 1:1 (NO
                    // fit-scale), centred in the widget, and CLIP anything past the widget
                    // edge. Authors size + place content with the node graph
                    // (Image.Transform), and overflow is cut off — instead of Display
                    // auto-fitting every output to ITS OWN aspect (which made a square
                    // source look "1:1 / letterboxed" inside a wide widget). Frame-sized
                    // output (Text.Render, an image transformed into the frame) is exactly
                    // widget-sized, so it fills 1:1 as before. The dense backing of a
                    // supersampled source is downsampled by the 4-arg draw → stays sharp.
                    const dx = renderRect.x + (rw - iw) / 2;
                    const dy = renderRect.y + (rh - ih) / 2;
                    ctx.save();
                    ctx.beginPath();
                    ctx.rect(renderRect.x, renderRect.y, rw, rh);
                    ctx.clip();
                    if (iw > 0 && ih > 0) ctx.drawImage(result.image, dx, dy, iw, ih);
                    ctx.restore();
                    debugLog('renderWidgetTrigger.painted', { widgetId: widget.id, iw, ih });
                } else {
                    // P0 #2 — no image came out of the pipeline. Paint a hint card
                    // so the OBS preview surfaces the broken state instead of an
                    // invisible empty rect. The author sees this immediately and
                    // knows whether to wire something or fix the loader's path.
                    //
                    // Distinguish the THREE distinct empty cases so "majority of
                    // nodes only say load failed" stops being a catch-all: many
                    // palette nodes (Math / Vector / Scalar / String / Color / Time /
                    // Convert …) output DATA, not an image, so wiring them straight
                    // into Display can never render — that is "not an image", not a
                    // failed load. A genuine null (empty Image.Load Path, a 404, an
                    // eval throw) stays "load failed"; an unwired sink stays "no
                    // Image input".
                    let reason;
                    if (!inLink)              reason = 'no Image input';
                    else if (result == null)  reason = 'load failed';
                    else                      reason = 'input is not an image';
                    // Overlay-only widget: don't paint the "no Image input" card behind the
                    // DOM overlay (a genuine load failure / wrong-type input still surfaces).
                    if (!(reason === 'no Image input' && hasOverlaySink))
                        paintDisplayDiagnostic(renderRect, reason);
                    debugLog('renderWidgetTrigger.empty', {
                        widgetId: widget.id,
                        reason,
                        hadLink: !!inLink,
                        resultType: result == null ? 'null' : typeof result,
                    });
                }
            }

            // Audio.Play sinks — visited like Visual.Complete (no return value matters,
            // the visit IS the side-effect that schedules playback). All Audio.Play
            // nodes evaluate so a graph can drive multiple parallel audio streams.
            // [P1 swarm-audit 2026-05-29] guard trigger.graph / trigger.graph.Nodes —
            // a trigger may carry no graph (empty/legacy widget). Matches the
            // `(trigger.graph && trigger.graph.Nodes) || []` pattern used elsewhere.
            //
            // ── V7: TWO KNOWN LIMITS, DOCUMENTED AND NOT TO BE "FIXED" ──────────────
            //
            //  1. This pass is NOT Result.If-gated. It sits outside every Display /
            //     Result.If branch and pulls its own upstream via findLinkTo(id,'Audio'),
            //     so no image-side gate can suppress it: every Audio.Play in the graph
            //     plays, whichever visual branch won. This is STRUCTURAL, not an
            //     oversight — Result.If's In/Out are Image-typed and Audio.Play's only
            //     input is Audio-typed, so the barrier cannot be placed in an audio
            //     chain at all.
            //  2. Audio.Play is a per-graph singleton: the widget editor rejects a
            //     second drop per trigger (WidgetGraphCanvas has mirror gates on spawn,
            //     duplicate and paste), even though this loop would happily run several.
            //
            // Together those two are exactly WHY String.Select exists. "One sound per
            // alert kind" cannot be expressed as N gated Audio.Play nodes; it is
            // expressed as ONE Audio.Load + ONE Audio.Play whose Path is selected BY
            // VALUE upstream. Do not attempt branch-gated audio — the type systems do
            // not meet, and an audio-typed Result.If clone would be a second, divergent
            // gate semantics for authors to learn.
            const audioSinks = meta.audioSinks;
            for (const audioSink of audioSinks) {
                try { await ev.evalNodeOutput(audioSink.Id, ''); }
                catch (e) { console.warn('[Visualist] Audio.Play sink eval failed:', e); }
            }

            // WebOverlay.Custom sinks — DOM-overlay track. Visited like Audio.Play: the
            // visit mounts/refreshes a live HTML+CSS element over the widget rect (browser
            // animates it natively; no rAF promotion needed). Multiple overlay nodes per
            // trigger are allowed, but the editor caps drops at one per trigger.
            const overlaySinks = meta.overlaySinks;
            const overlayIds = new Set();
            for (const ov of overlaySinks) {
                try { await evalWebOverlay(ev, widget, ov); overlayIds.add(ov.Id); }
                catch (e) { console.warn('[Visualist] WebOverlay.Custom sink eval failed:', e); }
            }
            // Tear down any overlay this widget mounted for a prior trigger / deleted node.
            reconcileWidgetOverlays(widget, overlayIds);

            // V15 — Player.Embed sinks, the second DOM-overlay track pass. Same shape as the
            // block above, and separate from it on purpose: the two keep independent entry
            // maps because their teardown is not the same operation (a player also has to
            // stop timers and destroy a live iframe), and folding them would make one
            // reconcile sweep responsible for two lifecycles.
            const playerSinks = meta.playerSinks;
            const playerIds = new Set();
            for (const pl of playerSinks) {
                try { await evalPlayerEmbed(ev, widget, pl); playerIds.add(pl.Id); }
                catch (e) { console.warn('[Visualist] Player.Embed sink eval failed:', e); }
            }
            // Tears the frame down on the revert-to-onStartup that ends a clip shoutout.
            reconcileWidgetPlayers(widget, playerIds);

            // Visual.Complete sink — if present, "visiting" it via upstream evaluation IS the
            // completion signal. The default no-op visit just resolves any upstream chain so
            // graph authors can gate completion timing (e.g., chain through a future Wait node).
            const completeSink = meta.completeSink;
            if (completeSink) {
                // Evaluate upstream of the Complete sink so any side-effecting nodes execute.
                // We don't care about the value — reaching this point means the chain settled.
                await ev.evalAnyInputOf(completeSink);
                // V13 H1 — and NOW read the Payload pin. AFTER evalAnyInputOf, not instead of
                // it: that probe already walked every inbound link including this one, so the
                // read below lands on the Evaluator's memo and no upstream node — including a
                // side-effecting one — runs twice.
                const payload = await ev.resolveCompletionPayload(completeSink);
                if (payload === null) _widgetCompletionPayload.delete(widget.id);
                else                  _widgetCompletionPayload.set(widget.id, payload);
            } else {
                // No completion sink in THIS trigger's graph — drop whatever a previous trigger
                // of the same widget left behind, so a stale string cannot ride out on an
                // unrelated activation's ack.
                _widgetCompletionPayload.delete(widget.id);
            }

            // Per-widget animator — if any node in this trigger flagged itself as
            // animated (Video.Load, .gif Image.Load), keep redrawing this widget
            // until a different trigger arrives or the widget unbinds. `renderSeq` is what lets
            // the promotion be REJECTED when a newer render has already claimed the slot.
            promoteWidgetAnimator(widget, trigger, renderSeq);

            return true;
        } finally {
            // Sprint 7 — return every escape-canvas allocated during this
            // trigger to the pool. Runs AFTER ctx.drawImage has copied the
            // Display result's pixels into the visible context, so reuse
            // can't race the consumer. Wrapped in try/catch so a faulting
            // release (e.g. mid-shutdown when canvasPool is torn down)
            // doesn't escape the finally and mask the original error.
            try { ev.releaseEscapes(); } catch (e) { /* shutdown best-effort */ }
            // Restore the animator request flags this render displaced (see the save above).
            // On the normal path promoteWidgetAnimator has already consumed and cleared them, so
            // this puts back the false a top-level render started from — and on a throw it is the
            // only thing that clears them at all.
            _animatorRequestMediaForCurrentRender = _savedAnimatorRequestMedia;
            _animatorRequestTimeForCurrentRender  = _savedAnimatorRequestTime;
            // V13 A2 — spend the trace arm. In the FINALLY so a render that threw still reports
            // the nodes it did reach and still consumes the arm: leaving it armed would fire this
            // activation's trace off whichever unrelated render happened next (an animator frame,
            // a live patch), attributing it to the wrong trigger. Clearing BEFORE the send so a
            // throw inside the sender cannot leave the arm standing either.
            if (_traceThisRender) {
                _traceArmedWidgets.delete((widget && widget.id) || '');
                try { sendWidgetNodeTrace(widget, trigger, Array.from(ev.trace || [])); }
                catch (e) { /* best-effort; a diagnostic must never mask the render's own error */ }
            }
        }
    }

    // ── Dip-to-blank event transition ─────────────────────────────────────
    // When widget.transitionMs > 0, a trigger swap (and the idle revert) fade the
    // OLD widget content out to blank over the first half, then fade the NEW
    // content in over the second half — instead of the instant clearRect→drawImage
    // cut. transMs === 0 is the legacy instant path (byte-identical). Widget ids
    // mid-transition are held here so the animator loop skips the widget (it would
    // otherwise repaint live content over the dip; see promoteWidgetAnimator and
    // _animatorTick, which both consult this set).
    const _widgetTransitions = new Set();

    // Snapshot the widget's current on-canvas pixels into a pooled offscreen, in
    // DEVICE pixels — the main canvas backing store is logical×deviceScale and ctx
    // is dpr-scaled, so the source rect must be ×deviceScale (see applyResolution).
    function captureWidgetRegion(rect) {
        const dw = Math.max(1, Math.round(rect.width  * deviceScale));
        const dh = Math.max(1, Math.round(rect.height * deviceScale));
        const cap = canvasPool.acquire(dw, dh);
        const cctx = cap.getContext('2d');
        if (!cctx) return cap;
        cctx.setTransform(1, 0, 0, 1, 0, 0);
        cctx.clearRect(0, 0, cap.width, cap.height);
        try {
            cctx.drawImage(canvas,
                Math.round(rect.x * deviceScale), Math.round(rect.y * deviceScale), dw, dh,
                0, 0, dw, dh);
        } catch (e) { /* 0-size / tainted — caller just fades from blank */ }
        return cap;
    }

    // Draw a captured (device-px) snapshot into the logical widget rect at `alpha`.
    function drawCaptureAlpha(rect, cap, alpha) {
        if (!cap || alpha <= 0) return;
        ctx.save();
        ctx.globalAlpha = Math.max(0, Math.min(1, alpha));
        ctx.drawImage(cap, 0, 0, cap.width, cap.height, rect.x, rect.y, rect.width, rect.height);
        ctx.restore();
    }

    function playDipToBlank(rect, oldCap, newCap, transMs) {
        return new Promise(resolve => {
            const start = performance.now();
            const frame = () => {
                let p = transMs > 0 ? (performance.now() - start) / transMs : 1;
                if (p > 1) p = 1;
                ctx.clearRect(rect.x, rect.y, rect.width, rect.height);
                paintBackdrop(rect);
                if (p < 0.5) drawCaptureAlpha(rect, oldCap, 1 - p / 0.5);
                else         drawCaptureAlpha(rect, newCap, (p - 0.5) / 0.5);
                if (p < 1) requestAnimationFrame(frame);
                else resolve();
            };
            // First frame runs SYNCHRONOUSLY (p≈0 → OLD at full alpha), overwriting
            // the NEW that renderWidgetTrigger just painted, so the swap never
            // flashes NEW for a frame before the dip begins.
            frame();
        });
    }

    // Render `trigger` into `widget`, dipping through blank from the current
    // content when transMs > 0. Runs inside the caller's withWidgetLock.
    async function renderWithTransition(widget, trigger, transMs) {
        transMs = Number(transMs) || 0;
        if (!(transMs > 0)) { await renderWidgetTrigger(widget, trigger); return; }

        const rect = widgetRenderRect(widget);
        const oldCap = captureWidgetRegion(rect);
        let newCap = null;
        _widgetTransitions.add(widget.id); // pause the per-widget animator mid-dip
        try {
            await renderWidgetTrigger(widget, trigger); // paints NEW (animator paused)
            newCap = captureWidgetRegion(rect);
            await playDipToBlank(rect, oldCap, newCap, transMs);
        } catch (e) {
            console.warn('[Visualist] transition failed; cutting:', e);
        } finally {
            _widgetTransitions.delete(widget.id);
            try { canvasPool.release(oldCap); } catch (e) { }
            if (newCap) { try { canvasPool.release(newCap); } catch (e) { } }
            // Final live render: replaces the NEW snapshot with live content and
            // (re-)arms the animator slot now the dip is finished. For a static NEW
            // this is an identical redraw; for video/GIF/keyframes it registers the
            // slot that promoteWidgetAnimator refused to create mid-dip.
            //
            // The clock is re-stamped and written HERE, for two reasons that both showed up as
            // "the intro doesn't play":
            //   • the activation was stamped BEFORE the dip, so a transitionMs-length slice of
            //     the widget's own track had already elapsed by the time its first live frame
            //     existed. At the 1000 ms transition ceiling any sub-second intro was skipped
            //     outright. Re-stamping when the dip ENDS starts the track at the moment the
            //     content actually appears.
            //   • this render site did not establish triggerContext.timeMs at all, and the dip
            //     awaits rAF for up to a second — during which the animator loop writes timeMs
            //     for OTHER widgets. So the first frame after a dip sampled a different widget's
            //     clock, which for a late-running page clamped every keyframe to its last value.
            //     Mirror the other render sites and write this widget's own cursor.
            _stampWidgetActivation(widget.id);
            _applyWidgetTimeCursor(widget.id);
            try { await renderWidgetTrigger(widget, trigger); } catch (e) { /* best-effort */ }
        }
    }

    // ── Media helpers (paths, video/audio pools, per-widget animator) ──────

    // Resolve a bare relative Path attribute through Hub's /media route. Paths
    // already absolute (/foo, http*) or data: URLs pass through untouched so
    // existing layers and the URL cache route keep working.
    /// P0 #2 — paints a "Display: <reason>" diagnostic card inside the widget
    /// rect when the trigger's Display sink couldn't produce an image. Uses
    /// the gold Selection token for a 1px dashed border so the broken state
    /// is unmistakeable in OBS without being visually noisy. Reasons used by
    /// the caller: "no Image input" (graph not wired), "load failed" (link
    /// present but evaluator returned null — usually empty Path or missing
    /// file). Sized off the widget rect so it never overflows.
    function paintDisplayDiagnostic(rect, reason) {
        if (!rect || rect.width <= 0 || rect.height <= 0) return;
        ctx.save();
        try {
            ctx.lineWidth   = 1;
            ctx.strokeStyle = 'rgba(255, 215, 0, 0.85)'; // Phoenix Controls theme — Selection
            if (typeof ctx.setLineDash === 'function') ctx.setLineDash([6, 4]);
            ctx.strokeRect(rect.x + 0.5, rect.y + 0.5, rect.width - 1, rect.height - 1);
            if (typeof ctx.setLineDash === 'function') ctx.setLineDash([]);

            // Caption — centred. Falls back to the rect's smaller axis so it
            // stays readable on small widgets without overflowing on big ones.
            const fontPx = Math.max(11, Math.min(20, Math.floor(rect.height / 8)));
            ctx.fillStyle = 'rgba(230, 220, 150, 0.95)'; // AccentValue
            ctx.font = `${fontPx}px "Segoe UI", sans-serif`;
            ctx.textAlign    = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(`Display: ${reason}`, rect.x + rect.width / 2, rect.y + rect.height / 2);
        } finally {
            ctx.restore();
        }
    }

    // THE single media-source resolver. All three local-file loaders ride it —
    // evalImageLoad, evalVideoLoad and evalAudioLoad, and nothing else — so image, video
    // and audio inherit one and the same server-side guard: a relative path becomes
    // /media/<segments>, which HUDServer routes into ServeFileFromRootAsync, whose
    // Path.GetFullPath + root-prefix comparison 403s anything that escapes MediaDirectory.
    // (Image.LoadUrl and WebSource deliberately do NOT come here; they go through the
    // /asset/url proxy, which does its own SSRF + MIME validation.)
    //
    // ★ THE THREE ESCAPE HATCHES, and who is allowed to use them.
    //
    // A leading '/', an http(s): URL and a data: URI all pass through UNPROXIED and therefore
    // un-guarded — that is what isNonRelativeMediaPath below names. Those three exist for the
    // streamer: an author who types an absolute path or a CDN URL into the Path box has
    // deliberately pointed the overlay off-tree, and taking that away would break authored
    // work. They keep working, unchanged, for an ATTRIBUTE value.
    //
    // They must NOT be reachable from a WIRED Path, because V7 made Path wirable and its own
    // headline chain wires Visual.Arg (i.e. eventData, i.e. a chat argument) into it:
    //
    //     viewer types "!sound https://attacker/x.mp3"
    //       → the script forwards it as Args1
    //       → Visual.Arg → String.Select → Audio.Load.Path
    //       → the streamer's OBS fetches an attacker-named URL
    //
    // which discloses the streamer's home IP to the attacker and plays arbitrary media on air
    // (a data: URL renders attacker content inline instead). So the rejection is provenance-
    // aware and lives in Evaluator._evalMediaPathSocket — NOT here: this function has no idea
    // where its argument came from, and a blanket refusal here would break the author case.
    // ONE predicate serves both so the pass-through set and the rejection set cannot drift.
    function isNonRelativeMediaPath(p) {
        return p.startsWith('/') || /^https?:/i.test(p) || p.startsWith('data:');
    }

    // THE single media-source resolver. All three local-file loaders ride it —
    // evalImageLoad, evalVideoLoad and evalAudioLoad, and nothing else — so image, video
    // and audio inherit one and the same server-side guard: a relative path becomes
    // /media/<segments>, which HUDServer routes into ServeFileFromRootAsync, whose
    // Path.GetFullPath + root-prefix comparison 403s anything that escapes MediaDirectory.
    // (Image.LoadUrl and WebSource deliberately do NOT come here; they go through the
    // /asset/url proxy, which does its own SSRF + MIME validation.)
    function resolveMediaPath(p) {
        if (!p) return p;
        if (isNonRelativeMediaPath(p)) return p;
        return `/media/${p.split('/').map(encodeURIComponent).join('/')}`;
    }

    // Page-lifetime dedupe for the rejected-wired-media-path diagnostic, keyed
    // `<nodeId>|<value>`. PAGE-level, never per-Evaluator: an Evaluator is built per render, so a
    // latch living on it dedupes once per RENDER, and a rejected path on a widget in the animator
    // loop would push a TRIGGER_DIAGNOSTIC frame plus a console.warn at frame rate. A rejection is
    // a STATIC condition (this node, this value) rather than a per-fire event, so one report per
    // (node, value) per page is both sufficient and bounded. Cleared with the rest of the page
    // state in _disposeGlobals.
    const _reportedRejectedMediaPaths = new Set();

    // Page-lifetime dedupe for the missing-arg diagnostics (Result.If's gate and Text.Render's
    // {ArgsN} substitution), keyed `<nodeId>|<arg>|<kind>`.
    //
    // ★ THIS USED TO LIVE ON THE EVALUATOR and that was the bug: `loggedMissingArgs` was described
    // as "once per fire", but a fresh Evaluator is constructed per renderWidgetTrigger, so its once
    // was really once per RENDER. A keyframed or animated widget whose Result.If arg is absent —
    // which is the NORMAL state on every onStartup render, every live patch and every animator tick,
    // because triggerContext.eventData is {} outside a trigger fire — therefore pushed a
    // TRIGGER_DIAGNOSTIC frame and a console.warn at frame rate into Hub's socket and System Log.
    // sendEvalDiagnostic's contract requires every caller to gate; this is the same page-scoped
    // shape its two siblings above already use, so the requirement now actually holds for all of
    // them.
    //
    // The cost is the same trade the other two latches accept: the same (node, arg, kind) going
    // missing a second time is not re-reported within one page. That is acceptable because the first
    // report already names the node and the arg to go and look at, and because the alternative is a
    // diagnostic so noisy it buries every other line in the log.
    const _reportedMissingArgs = new Set();

    // Pool of <video> elements keyed by node id. Created off-screen so video
    // decode runs but they don't affect layout. Reused across renders so the
    // playhead persists between frames. Capped — without the cap, a graph that
    // generates many short-lived Video.Load nodes (e.g. via macro instancing)
    // would keep accumulating <video> elements that the GPU never finishes
    // decoding for.
    const VIDEO_POOL_MAX = 16;
    const _videoPool = new Map();
    function _evictOldestPoolEntry(pool, max, teardown) {
        while (pool.size > max) {
            const it = pool.entries().next();
            if (it.done) break;
            const [oldestId, oldestEl] = it.value;
            pool.delete(oldestId);
            try { teardown(oldestEl); } catch { /* ignore element teardown errors */ }
        }
    }
    function _teardownVideoElement(v) {
        try { v.pause(); } catch { }
        try { v.removeAttribute('src'); v.load(); } catch { }
        try { if (v.parentNode) v.parentNode.removeChild(v); } catch { }
    }
    function ensureVideoElement(nodeId, src, opts) {
        let v = _videoPool.get(nodeId);
        if (v) {
            // LRU-promote so reads keep an active video alive past the cap.
            _videoPool.delete(nodeId);
        } else {
            v = document.createElement('video');
            v.crossOrigin   = 'anonymous';
            v.playsInline   = true;
            v.autoplay      = true;
            v.style.position = 'absolute';
            v.style.left = '-99999px'; // off-screen — only the canvas reads frames from it
            v.style.width = '1px'; v.style.height = '1px';
            document.body.appendChild(v);
        }
        _videoPool.set(nodeId, v);
        _evictOldestPoolEntry(_videoPool, VIDEO_POOL_MAX, _teardownVideoElement);
        if (v.dataset.src !== src) {
            v.dataset.src = src;
            v.src = src;
            // Source changed — reset the one-shot alpha probe so the next
            // evalVideoLoad re-checks the new file. Without this, an author
            // iterating on encoder settings would only ever see the probe
            // log for the first source they loaded into this node.
            delete v.dataset.alphaProbed;
        }
        v.loop  = !!opts.loop;
        v.muted = !!opts.muted;
        // play() is async and may reject if the user hasn't interacted with
        // the page; catch + ignore so we don't pollute the console on every
        // tick. OBS browser sources autoplay muted video by default.
        try { v.play().catch(() => { }); } catch { }
        return v;
    }

    // Pool of <audio> elements keyed by node id. Each Audio.Play sink owns one.
    // Capped for the same reason as the video pool above.
    const AUDIO_POOL_MAX = 32;
    const _audioPool = new Map();
    // nodeId → the generation value that last (re)started this node, as read from
    // _audioActivationGen for the OWNING WIDGET (the map is per widget — see it). A playback
    // is (re)started only when the current generation differs from this, so the animator /
    // Play loop / scrub re-renders within one activation never replay a finished one-shot,
    // and neither does a NEIGHBOUR widget's activation. Keyed by node id, which is a GUID and
    // therefore already widget-unique, so no re-keying was needed here.
    // Cleared on layer teardown (_disposeGlobals).
    //
    // V7 — the generation now latches the SOURCE as well as the start, because Path is
    // wirable and can resolve differently between two renders of one activation. See the
    // long block inside ensureAudioElementAndPlay.
    const _audioPlayedGen = new Map();
    function _teardownAudioElement(a) {
        try { a.pause(); } catch { }
        try { a.removeAttribute('src'); a.load(); } catch { }
        try { if (a.parentNode) a.parentNode.removeChild(a); } catch { }
    }
    function ensureAudioElementAndPlay(nodeId, src, opts) {
        const gen = opts.gen;
        // Only a NEW activation generation may (re)start this node's playback.
        const newActivation = _audioPlayedGen.get(nodeId) !== gen;

        let a = _audioPool.get(nodeId);
        if (a) {
            _audioPool.delete(nodeId);
        } else if (newActivation) {
            a = document.createElement('audio');
            a.crossOrigin = 'anonymous';
            a.preload = 'auto';
            document.body.appendChild(a);
        } else {
            // Same activation, and the one-shot already finished + auto-evicted
            // itself: a re-render must NOT recreate and replay it. This is the
            // exact path that made Loop = false audio loop. Nothing to do.
            return;
        }
        _audioPool.set(nodeId, a);
        _evictOldestPoolEntry(_audioPool, AUDIO_POOL_MAX, _teardownAudioElement);
        // Level and loop mode DO track every visit — the trigger master volume can change
        // between render ticks. The SOURCE deliberately does not; see below.
        a.volume = opts.volume;
        a.loop   = opts.loop;

        if (!newActivation) {
            // Re-render within the SAME activation. For a looping clip a.loop keeps
            // it going natively; for a one-shot we must not restart it. Either way,
            // never call play() again here — just keep the level in sync.
            //
            // ── V7: THE SOURCE IS LATCHED AT ACTIVATION ─────────────────────────────
            // Until V7 the src-change block sat ABOVE this return, and with a fixed
            // author-typed Path that was unreachable dead weight. A wirable Path makes it
            // reachable, and it was wrong in both directions:
            //
            //   • What it DID do: reassign a.src + a.load(), which STOPS the clip that is
            //     currently playing, and then return without play(). Audio died mid-alert
            //     and never came back. Not a spurious replay — a spurious silence.
            //   • What it must NOT do instead: re-source AND play(). renderWidgetTrigger
            //     re-runs per animation frame (V5 put a per-frame clock on this path) and
            //     on every WIDGET_UPDATE drag, and a wired path can legitimately resolve
            //     differently between two of those renders — a Var.Live key patched at
            //     1 Hz, a Time.* driven chain. Treating a changed src as "new" would
            //     therefore replay the clip at up to frame rate: the 2026-06-23
            //     "Loop = false audio loops" bug, restored and worse.
            //
            // So the gate distinguishes a path change from a re-trigger by NOT CONSULTING
            // THE PATH AT ALL. Only the activation generation — bumped by exactly three
            // message-driven sites (RUN_TRIGGER, its idle revert, SET_ACTIVE_TRIGGER) and
            // by nothing on the animator / clock / patch route — decides whether playback
            // starts. Within one activation the clip that started keeps playing to its own
            // end; a newly resolved path is picked up by the next genuine activation.
            //
            // ★ "ONE ACTIVATION PLAYS ONE CLIP" IS A PER-WIDGET STATEMENT, and saying it
            // without that qualifier was wrong for as long as the generation was a page-wide
            // counter. The invariant only holds because `gen` now comes from
            // audioActivationGen(widgetId): with one shared counter, activating widget B
            // inside widget A's 2 s hold bumped the number A's animator frames compare
            // against, so A's next frame read "new activation" and replayed A's one-shot
            // against whatever triggerContext B had just installed — one activation, two
            // plays, second one with the wrong clip. The scoping is what makes the sentence
            // true; do not re-collapse the map to a scalar.
            return;
        }
        // Genuine (re)activation for this node — adopt the source resolved for THIS
        // activation, stamp the generation, and (re)start. Re-sourcing lives here, inside
        // the new-activation branch, precisely so a mid-activation resolution change can
        // never touch the live element (see the block above). The guard still matters: a
        // re-trigger with the SAME clip must not re-load it, because load() would discard
        // the decoded buffer and re-fetch for no reason — currentTime = 0 below is what
        // replays it.
        if (a.dataset.src !== src) {
            a.dataset.src = src;
            a.src = src;
            try { a.load(); } catch { }
        }
        _audioPlayedGen.set(nodeId, gen);

        // Auto-evict on natural completion so non-looping one-shots don't
        // permanently occupy a pool slot until the cap pushes them out. Clear any
        // prior 'ended' listener first so rapid re-triggers of a still-playing
        // one-shot don't stack listeners.
        if (a._phxOnEnded) {
            try { a.removeEventListener('ended', a._phxOnEnded); } catch { }
            a._phxOnEnded = null;
        }
        if (!opts.loop) {
            const onEnded = () => {
                a.removeEventListener('ended', onEnded);
                a._phxOnEnded = null;
                if (_audioPool.get(nodeId) === a) _audioPool.delete(nodeId);
                _teardownAudioElement(a);
            };
            a._phxOnEnded = onEnded;
            a.addEventListener('ended', onEnded, { once: true });
        }
        // Restart from the top so a genuine re-trigger replays from the start
        // (play() alone is a no-op on an already-playing element).
        try { a.currentTime = 0; } catch { }
        try { a.play().catch(() => { }); } catch { }
    }

    // Animator opt-in for the render currently in progress, consumed by promoteWidgetAnimator at
    // the end of it. A widget with both flags down is demoted to render-once.
    //
    // TWO flags, not one, and the split is load-bearing:
    //   • MEDIA — an animated GIF, a <video>, a particle emitter. Unbounded by nature: only the
    //     source knows when it is done, and it never tells us.
    //   • TIME  — the trigger's keyframes / Time.* nodes (V5). BOUNDED when it is keyframes
    //     only, which is what lets the loop stop at meta.timeExtentMs.
    // A widget that raised MEDIA must never be stopped by the time bound, or a GIF beside a
    // 500 ms keyframed fade-in would freeze half a second after it appeared — the exact class of
    // "GIF renders static in OBS" defect this file has already been bitten by twice (0.12.27's
    // off-DOM decode and #6's competing Play loop). One shared boolean could not tell the two
    // reasons apart, so the bound could not have been added safely without this split.
    let _animatorRequestMediaForCurrentRender = false;
    let _animatorRequestTimeForCurrentRender  = false;
    function requestWidgetAnimator()     { _animatorRequestMediaForCurrentRender = true; }
    function requestWidgetTimeAnimator() { _animatorRequestTimeForCurrentRender  = true; }

    // Monotonic RENDER ORDER stamp. Incremented once at the top of renderWidgetTrigger and
    // captured into a local there, so every render carries a number that says where it sits in the
    // sequence of renders this page has STARTED — and every animator slot remembers the number of
    // the render that installed it (promoteWidgetAnimator).
    //
    // Why it exists: renderWidgetTrigger's save/restore of the request flags fixed the direction
    // where an overlapping render DEMOTED a widget, and made the opposite direction worse. Exact
    // interleave, all on ONE widget A:
    //
    //   1. the animator loop starts rendering A's onStartup and raises the time request,
    //   2. that render suspends on a cold image decode,
    //   3. a RUN_TRIGGER for A arrives, renders A's alert, and promotes a slot for the ALERT,
    //   4. the animator's finally RESTORES its saved request flag, its render resumes,
    //   5. …and it promotes A's OLD onStartup trigger over the fresh alert slot.
    //
    // Before the save/restore, step 5 merely STOPPED the animation. After it, the loop repaints the
    // OLD trigger's graph over the NEW content on stream and keeps doing it for the whole hold. A
    // slot may therefore only ever move FORWARD in render order; see the rejection in
    // promoteWidgetAnimator. There is no wraparound concern: at a sustained 60 renders/second this
    // stays exact for ~4.7 million years of Number.MAX_SAFE_INTEGER.
    let _renderSeq = 0;

    // Sprint 92 — Particles.Emit per-node state. Each Particles.Emit node keeps
    // its own active particle list + last-tick timestamp here, keyed by node id.
    // Survives across renders so particles flow continuously between triggers
    // (the global animator loop, which requestWidgetAnimator() opts the widget
    // into, drives the re-render). Capped at PARTICLE_HARD_CAP per node to keep a
    // runaway Rate from OOMing the browser when an author types 99999.
    const PARTICLE_HARD_CAP = 500;
    const _particleState = new Map(); // nodeId → { particles: [...], lastTickMs: number }

    // ── THE global animator loop ─────────────────────────────────────────────
    //
    // Registry of animated widgets. `_widgetAnimators` is now a pure registry — a slot no
    // longer owns a requestAnimationFrame handle, because there is exactly ONE loop for the
    // whole page and it walks this map.
    //
    //   widgetId → { widget, trigger, suspended,
    //                _consecutiveFailures, _suspendLogged }
    //
    // ★ Why one serial loop instead of N per-widget loops (what this replaced)
    //
    // triggerContext and activeTimeline are module-level SINGLETONS. Both are written at the
    // top of renderWidgetTrigger and read during evaluation — and evaluation awaits image
    // decodes, font loads and video seeks. With N concurrent rAF loops, widget B's render
    // overwrote those globals while widget A's render was suspended mid-decode, so A resumed
    // and sampled B's timeline. That hazard was already live for activeTimeline before any
    // clock existed; a per-widget timeMs write would simply have added a second victim.
    //
    // What serialising ACTUALLY buys, stated no wider than the code earns: the loop cannot race
    // ITSELF. Two animator frames never overlap, and neither do two widgets within one frame, so
    // the N-loops failure above is gone for every render this loop starts.
    //
    // It is NOT a proof that the two singletons are safe. This loop is the one render path that
    // does NOT take withWidgetLock, so it still interleaves with every MESSAGE-driven render —
    // including a RUN_TRIGGER on the SAME widget. An animator frame suspended mid-decode can
    // still resume after a trigger render has rebound activeTimeline / rewritten timeMs, and
    // paint one frame sampled against the other trigger's cursor.
    //
    // Wrapping the loop in withWidgetLock is NOT the fix and must not be attempted:
    // handleRunTrigger holds that lock across its entire 2000–60000 ms hold, so the triggered
    // widget's animation would freeze for the whole hold — it would break the feature this loop
    // exists for. The correct closure is one of:
    //   • a page-level render mutex taken around render BODIES only, never across the hold, or
    //   • threading timeMs + timeline through the Evaluator so there is no singleton to race.
    // Both are deliberately NOT attempted here. The second is why threading was rejected for V5:
    // attrAnimated / attrAnimatedColor are free functions called from 67 sites, none via `this.`,
    // on the hottest path in the file — unverifiable headlessly, and it would have left
    // activeTimeline broken anyway. Until one of them lands, the residual exposure is exactly
    // those two values (a frame sampled at another trigger's cursor, worst case one wrong frame
    // that the next frame corrects). The animator REQUEST flags are no longer part of it —
    // renderWidgetTrigger saves and restores them, so an interleave can no longer donate or
    // consume a promotion.
    //
    // It costs no extra work: every animated widget was already re-rendering once per frame and
    // they all paint into ONE shared 2D context. Only the ordering changed, from concurrent to
    // serial, which for a shared canvas is strictly safer.
    //
    // The loop is also the single tick source for the 1 Hz clock heartbeat (decision record
    // D10). That beat used to be its own setInterval whose consumer pass could interleave with
    // an animator render — same singleton, same hazard. Riding this loop it cannot.
    const _widgetAnimators = new Map();
    const MAX_CONSECUTIVE_FAILURES = 5;
    const CLOCK_BEAT_INTERVAL_MS   = 1000;

    /// The trigger payload a promoting render ran under, captured onto its animator slot.
    ///
    /// ★ WHY A SLOT NEEDS ONE. triggerContext.eventData / .triggerName are page-wide
    /// singletons, but an animator slot is per widget — so an animator frame for widget A
    /// re-evaluated A's graph against whatever eventData the page had LAST written, which on a
    /// multi-widget layer is routinely somebody else's: widget B's RUN_TRIGGER overwrites it
    /// mid-hold, and the 1 Hz clock beat and every live-patch pass blank it to {} outright
    /// (_renderConsumerPass does so deliberately). Every eventData reader on the animator path
    /// then reads the wrong activation — Result.If gates on a foreign arg, substituteArgs
    /// drops the donor name, and after V7 a wired Audio.Load / Image.Load Path resolves to a
    /// DIFFERENT FILE than the activation the frame belongs to.
    ///
    /// Snapshotting at promotion is the fix in the same shape V5 used for the clock: the slot
    /// already remembers WHICH trigger to re-render, so it also remembers the payload that
    /// trigger was activated with, and the loop restores both before each frame.
    ///
    /// The eventData OBJECT is stored by reference, not cloned, and that is safe by
    /// construction: every writer installs a fresh object (handleRunTrigger takes
    /// `msg.eventData || {}` off the inbound message; the idle revert, the scrub / play /
    /// active-trigger handlers and the consumer passes all assign a brand-new `{}`) and
    /// nothing in this file mutates the object in place. A clone would cost a JSON round trip
    /// per promotion for no additional guarantee.
    function _captureActivationContext() {
        return {
            triggerName:   triggerContext.triggerName || '',
            eventData:     triggerContext.eventData || {},
            eventDataJson: triggerContext.eventDataJson || '{}',
        };
    }

    /// Re-establishes the payload of the activation a slot belongs to, immediately before the
    /// animator renders it. The mirror of _applyWidgetTimeCursor for event data: same reason
    /// (a per-widget value living in a page-wide singleton), same placement (once, right
    /// before the render that reads it).
    function _applyActivationContext(slot) {
        const a = slot && slot.activation;
        if (!a) return;
        triggerContext.triggerName   = a.triggerName;
        triggerContext.eventData     = a.eventData;
        triggerContext.eventDataJson = a.eventDataJson;
    }

    // How much wall clock ONE tick may spend rendering before it yields — about one 60 Hz frame.
    //
    // Without a budget the loop's cadence was 1 / (sum of every animated widget's render time),
    // because the next rAF is only requested in the tick's finally. That is the cost of making
    // the loop serial, and the per-widget ceiling (LIVE_PASS_WIDGET_TIMEOUT_MS = 5000) is what
    // made it unbounded in the bad case: N simultaneously stalled widgets cost N × 5 s on ONE
    // tick, during which NOTHING animates and the clock beat does not fire at all. The N
    // per-widget loops this replaced at least let the fast widgets keep 60 Hz.
    //
    // The budget restores that property without giving up serial rendering: once a tick is over
    // budget it stops and lets the browser present, and the next tick resumes from where it
    // stopped (see _animatorResumeId). Every widget still gets served, just across more ticks —
    // and the clock beat, which runs after the render walk, is reached on every tick either way.
    const ANIMATOR_FRAME_BUDGET_MS = 24;

    // Widget ids whose animator render has not settled yet. Keyed by WIDGET, not by slot, so
    // replacing a slot mid-render (a trigger swap) cannot start a second concurrent render of
    // the same widget — the N per-widget loops could, and only got away with it because a
    // superseded tick returned on its identity check before rendering again.
    const _animatorInFlight = new Set();

    let _animatorHandle       = 0;      // pending tick handle (0 = nothing scheduled)
    let _animatorHandleIsRaf  = false;  // which canceller the handle belongs to
    let _animatorTickInFlight = false;  // a tick's async body is running right now
    let _clockBeatLastMs      = 0;      // _nowMs() of the last clock beat
    let _clockWidgetsPresent  = false;  // does `layer` carry a Clock.Now widget at all
    // Round-robin cursor for the frame budget: the widget id the NEXT tick should start from,
    // or null for "start at the beginning". Stored as an ID rather than an index because the
    // registry can gain or lose slots between ticks, and an index would then resume at an
    // unrelated widget (or skip one silently, which is how a widget stops animating for no
    // visible reason). A resume id that is no longer registered falls back to the start.
    let _animatorResumeId     = null;

    /// True when at least one registered widget wants a repaint every frame. A slot stays in the
    /// map but is NOT work once it is settled — its keyframe track played out, or a design-time
    /// transport pinned its cursor so the loop cannot change its picture (see _animatorTick) —
    /// and likewise once the circuit breaker has tripped it.
    function _animatorHasFrameWork() {
        for (const [, slot] of _widgetAnimators)
            if (!slot.suspended && !slot.settledAtExtent) return true;
        return false;
    }

    /// Schedules the next tick, picking the cheapest source for the work that is actually
    /// pending.
    ///
    /// rAF when a widget needs a frame-rate repaint. A plain timeout, sized to the next beat,
    /// when the ONLY pending work is the 1 Hz clock heartbeat: a Clock.Now widget on an
    /// otherwise static layer must not cost 60 wakeups a second to repaint once. The
    /// setInterval this consolidated woke exactly once per second, and Manifesto §4.10's
    /// "inactive layers cost zero" is a promise about idle layers specifically — riding rAF
    /// unconditionally would have quietly traded that away for tidier code.
    ///
    /// Nothing pending at all ⇒ the loop stays stopped. Every path that creates work calls back
    /// in here (promoteWidgetAnimator, _refreshAnimatorDemand, resumeAllAnimations, and the
    /// tick's own finally), so there is no state from which the loop can fail to restart.
    ///
    /// ★ FRAME WORK PREEMPTS A PENDING CLOCK BEAT, and that is not an optimisation — it is what
    /// makes animation start on time. A pending handle normally means "already scheduled, nothing
    /// to do", so this used to return on any handle at all. But the only non-rAF handle this
    /// function creates is the clock-beat timeout, which is armed up to CLOCK_BEAT_INTERVAL_MS
    /// (1000 ms) ahead — so on a layer that carries a Clock.Now widget, a widget promoted AFTER
    /// the beat was armed could not get a frame until that timeout fired. Every animation on such
    /// a layer started 0–1000 ms late, and any track SHORTER than the delay rendered exactly one
    /// frame — at its FINAL keyframe pose, because the extent bound then retired it immediately.
    /// The bootstrap paint hit it every time: _refreshAnimatorDemand arms the loop before
    /// renderAll's first promotion exists.
    function _ensureAnimatorLoop() {
        if (_animationsPaused) return;
        // A tick is mid-body: its finally calls back in here, so there is nothing to schedule and
        // nothing to preempt (a running tick holds no handle).
        if (_animatorTickInFlight) return;
        if (_animatorHandle !== 0) {
            // Already on rAF, or the pending timeout is still the cheapest correct source
            // (no frame work) ⇒ leave it alone.
            if (_animatorHandleIsRaf || !_animatorHasFrameWork()) return;
            // Frame work appeared while a clock-beat timeout was pending: drop the timeout and
            // fall through to rAF. Losing nothing — the rAF path re-derives the beat from
            // _clockBeatLastMs at the end of every tick.
            _stopAnimatorLoop();
        }
        if (_animatorHasFrameWork()) {
            _animatorHandleIsRaf = true;
            _animatorHandle      = requestAnimationFrame(_animatorTick);
            return;
        }
        if (!_clockWidgetsPresent) return;
        const wait = Math.max(0, CLOCK_BEAT_INTERVAL_MS - (_nowMs() - _clockBeatLastMs));
        _animatorHandleIsRaf = false;
        _animatorHandle      = setTimeout(_animatorTick, wait);
    }

    function _stopAnimatorLoop() {
        if (_animatorHandle === 0) return;
        try {
            if (_animatorHandleIsRaf) cancelAnimationFrame(_animatorHandle);
            else                      clearTimeout(_animatorHandle);
        } catch { /* ignore */ }
        _animatorHandle = 0;
    }

    /// Re-derives whether the layer carries a Clock.Now widget, SEEDS the animator for every
    /// time-consuming widget, prunes activation stamps for widgets that no longer exist, and
    /// (re)starts the loop if there is now work.
    ///
    /// Called at the two — and only two — moments `layer` is replaced: the bootstrap fetch and
    /// softReloadLayer. Clock.Now is the one live reader with no Hub producer at all, so if this
    /// is not called nothing else will ever schedule a frame for it.
    ///
    /// ★ The SEED (widgetConsumesTime) exists because the loop's start must not depend on
    /// renderAll's promotions having completed. renderAll is not awaited at bootstrap and it
    /// paints widgets one at a time with image decodes in between, so the first promotion can be
    /// tens to hundreds of ms out — and until one exists the loop has no frame work, so it arms
    /// the 1 Hz clock timeout instead and (before the preemption above) sat on it. Seeding is
    /// the positive half: the moment the layer is known, every keyframed widget is registered, so
    /// _ensureAnimatorLoop schedules rAF straight away and a short intro track actually plays
    /// instead of appearing once at its last keyframe.
    ///
    /// Seeding cannot promote anything wrongly: the slot is provisional, and the render latch
    /// (requestWidgetTimeAnimator → promoteWidgetAnimator) re-decides it on the very first frame
    /// — a widget whose CURRENT trigger has no keyframes raises nothing and is demoted out again.
    /// The cost of a wrong seed is one render, once per layer load.
    function _refreshAnimatorDemand() {
        let present = false;
        if (layer && Array.isArray(layer.widgets)) {
            const alive = new Set();
            for (const w of layer.widgets) {
                if (!w) continue;
                alive.add(w.id);
                // Per-WIDGET containment, the same shape renderAll and _renderConsumerPass use:
                // this walk touches every widget's whole trigger list (the clock rollup and the
                // seed both do), and one malformed widget must cost at most its own animation. The
                // element guards downstream make a throw here unlikely; this makes it survivable
                // even if a future reader forgets one — the caller is a bootstrap `.then` and a
                // soft reload, and neither may lose its remaining work over one bad widget.
                try {
                    if (!present && widgetConsumesClock(w)) present = true;
                    _seedWidgetAnimator(w);
                } catch (e) {
                    console.warn('[Visualist] animator demand scan failed for widget',
                        w && w.id, e);
                }
            }
            // Bound the activation-stamp map — and the design-time ownership map, which is fed by
            // every playhead move — to widgets that still exist, so a long editing session of
            // add/delete/save cycles can't grow either without limit.
            for (const id of Array.from(_widgetActivationStart.keys()))
                if (!alive.has(id)) _widgetActivationStart.delete(id);
            for (const id of Array.from(_designTimeClockOwners.keys()))
                if (!alive.has(id)) _designTimeClockOwners.delete(id);
        }
        _clockWidgetsPresent = present;
        _ensureAnimatorLoop();
    }

    /// Registers a provisional animator slot for `widget` when its graphs are time-consuming and
    /// nothing has promoted it yet. Mirrors promoteWidgetAnimator's refusals so a seed can never
    /// do something a promotion would have declined:
    ///   • an existing slot is never touched (its breaker state / played-out mark / media flag
    ///     carry information a seed does not have),
    ///   • a widget the design-time PLAY transport owns is left to that transport (#6),
    ///   • a widget mid dip-to-blank is left to renderWithTransition's final render,
    ///   • an invisible widget is skipped — in `?widget=` preview mode the loop must render only
    ///     the filtered widget, and unlike promotion (which can only happen inside a render, and
    ///     renders only happen for visible widgets) a seed has no such implicit gate.
    ///
    /// It also refuses a widget whose clock a DESIGN-TIME transport owns, because the seed would
    /// buy nothing there: the loop cannot move a pinned cursor, so the slot's only effect would be
    /// one duplicate full-graph render racing the paint that is already happening. Those widgets
    /// still get a slot the moment a render promotes one (media needs it) — exactly as before.
    function _seedWidgetAnimator(widget) {
        if (!widget || !widget.id) return;
        if (_widgetAnimators.has(widget.id)) return;
        if (!isWidgetVisible(widget)) return;
        if (!_productionClockOwnsWidgetTime(widget.id)) return;
        if (_playState && _playState.widget && _playState.widget.id === widget.id) return;
        if (_widgetTransitions.has(widget.id)) return;
        if (!widgetConsumesTime(widget)) return;
        const trigger = defaultRenderTrigger(widget);
        if (!trigger) return;
        _widgetAnimators.set(widget.id, {
            widget, trigger,
            // A seed happens OUTSIDE any render, so there is no activation to capture — and
            // capturing the ambient singleton here would be exactly the bug the snapshot
            // exists to prevent (a seeded onStartup slot inheriting some other widget's
            // alert payload). An empty payload is what an onStartup render legitimately sees;
            // the first real render re-decides the slot and captures for real.
            activation:      { triggerName: trigger.name || '', eventData: {}, eventDataJson: '{}' },
            // Time-only until a render proves otherwise: media is raised from inside the render
            // (promoteWidgetAnimator's sticky arm flips this off), and guessing media here would
            // hand the widget an unbounded loop the extent bound could never stop.
            timeOnly:        true,
            suspended:       false,
            settledAtExtent: false,
            // Render order 0 — a seed happens OUTSIDE any render, and it is provisional by
            // contract, so it must never win promoteWidgetAnimator's stale-promotion check against
            // a real render (whose seq is always >= 1). See that check.
            renderSeq:       0,
            _consecutiveFailures: 0,
            _suspendLogged:       false,
        });
    }

    /// QC28-03 — circuit breaker. A broken widget used to spam the console at full rAF rate
    /// forever; after N consecutive throws the slot is suspended so one broken widget cannot pin
    /// a core and flood logs. The slot STAYS in the map so a future promoteWidgetAnimator() with
    /// a different trigger can replace it (and resumeAllAnimations clears the trip) — it is
    /// simply skipped by the loop until then.
    function _noteAnimatorFailure(slot, err) {
        slot._consecutiveFailures++;
        console.warn('[Visualist] widget animator render failed:', err);
        if (slot._consecutiveFailures < MAX_CONSECUTIVE_FAILURES) return;
        slot.suspended = true;
        if (slot._suspendLogged) return;
        slot._suspendLogged = true;
        console.error('[Visualist] suspending widget animator after',
            MAX_CONSECUTIVE_FAILURES, 'consecutive failures; widget=', slot.widget && slot.widget.id);
    }

    /// One tick: render animated widgets in turn — as many as the frame budget allows, resuming
    /// where the previous tick stopped — then beat the clock if a second has passed. Awaiting each
    /// render before starting the next IS the safety property — see the block comment above.
    async function _animatorTick() {
        _animatorHandle       = 0;
        _animatorTickInFlight = true;
        try {
            if (_animationsPaused) return;

            // Snapshot the entries: promoteWidgetAnimator runs INSIDE the renders we await and
            // can add or drop slots, and mutating a Map mid-iteration is exactly the kind of
            // thing that skips a widget silently.
            const entries = Array.from(_widgetAnimators.entries());
            // Resume point for the frame budget (round-robin, by widget id — see
            // _animatorResumeId). A resume id that is no longer registered restarts at 0 rather
            // than guessing, which costs at most one re-served widget.
            let startAt = 0;
            if (_animatorResumeId !== null) {
                const at = entries.findIndex(e => e[0] === _animatorResumeId);
                if (at >= 0) startAt = at;
                _animatorResumeId = null;
            }
            const walkStartedMs = _nowMs();
            for (let n = 0; n < entries.length; n++) {
                const [widgetId, slot] = entries[(startAt + n) % entries.length];
                // Identity check — this slot may have been retired, or superseded by one for a
                // different trigger, while we awaited an earlier widget in this same frame.
                if (_widgetAnimators.get(widgetId) !== slot) continue;
                if (slot.suspended) continue;
                // Its keyframe track has played out and nothing else in the graph is unbounded,
                // so every further frame would redraw identical pixels. Set below; cleared by
                // _stampWidgetActivation when the widget is genuinely re-triggered.
                if (slot.settledAtExtent) continue;
                // The design-time PLAY transport owns this widget's clock and its repaints (#6).
                if (_playState && _playState.widget && _playState.widget.id === widgetId) continue;
                // A dip-to-blank transition owns the widget: keep the slot registered (so the
                // loop resumes it if it isn't replaced) but don't repaint live content over the
                // fade. renderWithTransition's final render re-promotes.
                if (_widgetTransitions.has(widgetId)) continue;
                // A render from an earlier frame has still not settled — a stalled remote decode
                // is the usual cause. Skip rather than starting a SECOND concurrent render of
                // the same widget: the N per-widget loops got that property for free by only
                // scheduling their next frame after their own await resolved.
                if (_animatorInFlight.has(widgetId)) continue;

                // The per-widget activation clock, and the bound that lets this loop STOP.
                //
                // What changed in V5 is the DESIGN-TIME arm: it now settles a time-only slot
                // unconditionally, without consulting the extent at all. That is what fixed the
                // embedded `?widget=` preview, where _productionClockOwnsWidgetTime is always false
                // — the whole block used to live inside that gate, so nothing ever settled, and
                // every keyframed widget opened in the widget editor got a permanent
                // display-refresh-rate FULL-GRAPH render loop (image decodes, blur rasterisation,
                // canvas-pool churn) redrawing identical pixels. That preview repainted only on
                // SCRUB / PLAY / SET_ACTIVE_TRIGGER before V5, and it does again.
                //
                // The extent read stays INSIDE the production gate because that is the only arm
                // that uses it: the design-time settle needs no bound (see below). Hoisting it out
                // bought nothing and implied a coupling the code does not have.
                if (_productionClockOwnsWidgetTime(widgetId)) {
                    const extent = getTriggerMeta(slot.widget, slot.trigger).timeExtentMs;
                    let t = _widgetTimeMs(widgetId);
                    if (extent !== null && t >= extent) {
                        // Clamp to the final keyframe — keyframeSampleScalar would clamp anyway,
                        // so this is the same picture stated honestly — and if time was the ONLY
                        // reason this widget is in the loop, this is its LAST frame.
                        t = extent;
                        if (slot.timeOnly) slot.settledAtExtent = true;
                    }
                    triggerContext.timeMs = t;
                } else {
                    // A design-time transport owns this widget's cursor, so paint at the value that
                    // transport PINNED. Not "leave the singleton alone": between two animator
                    // frames this loop writes timeMs for every OTHER widget it serves, so skipping
                    // the write would sample this widget at a neighbour's clock — the hazard the
                    // whole two-owner rule exists to prevent. _applyWidgetTimeCursor holds the one
                    // copy of that rule (and writes nothing when nobody has pinned a value, e.g.
                    // the untouched `?widget=` preview or a widget that is not the one being
                    // played).
                    _applyWidgetTimeCursor(widgetId);
                    if (slot.timeOnly) {
                        // The pinned cursor changes ONLY when a SCRUB / PLAY / SET_ACTIVE_TRIGGER
                        // message arrives, and every one of those re-renders the widget itself. So
                        // for a time-only slot the picture is a pure function of a value this loop
                        // cannot move — one frame from now it would redraw identical pixels
                        // forever, whether the pinned cursor sits before the extent or past it.
                        // Hence no extent read here: the stop is STRONGER than the bound. Settle
                        // after this frame (the render below still happens, so a slot seeded by
                        // _refreshAnimatorDemand does get its paint) and hand the widget back to
                        // the message paths, which is what drives a design preview.
                        //
                        // A slot that also wants MEDIA is never settled here — a GIF / video /
                        // particle emitter still needs frames at design time, and `timeOnly` is
                        // exactly the flag that tells the two apart.
                        slot.settledAtExtent = true;
                    }
                }

                // Re-establish the PAYLOAD of the activation this slot belongs to, for the same
                // reason the block above re-establishes its clock: triggerContext.eventData is a
                // page-wide singleton that a neighbour's RUN_TRIGGER, the 1 Hz clock beat and
                // every live-patch pass all overwrite, so without this an animator frame
                // re-evaluates THIS widget's graph against a FOREIGN activation's arguments.
                // After V7 that also picks a foreign file: a wired Audio.Load / Image.Load Path
                // resolves through Visual.Arg → eventData.
                _applyActivationContext(slot);

                _animatorInFlight.add(widgetId);
                // Failure bookkeeping hangs off the REAL render promise, not off the race below,
                // so a timeout is never miscounted as a render failure and never trips the
                // breaker. Attaching both handlers here also means the promise is always
                // handled, even when the race gives up on it.
                const settled = renderWidgetTrigger(slot.widget, slot.trigger).then(
                    () => { _animatorInFlight.delete(widgetId); slot._consecutiveFailures = 0; },
                    e  => { _animatorInFlight.delete(widgetId); _noteAnimatorFailure(slot, e); });
                // Bounded (V4's per-widget render timeout): one never-settling render must not
                // stall the whole loop, because every other widget AND the clock beat queue
                // behind it now. The inFlight guard above is what keeps the cost of a stall to
                // one slow frame instead of one slow frame per frame. Its own warn latch, so a
                // stalled live pass cannot silence the animator's report (or vice versa).
                await _raceWidgetRender(settled, widgetId, 'animator', _animatorStallLatch);

                // Frame budget — yield once this tick has spent about a frame's worth of wall
                // clock rendering, and remember who to serve first next time. Without this the
                // loop's cadence was 1 / (sum of every widget's render time), so a handful of slow
                // widgets dragged the FAST ones down with them and N stalled ones could hold the
                // tick (and the clock beat below) for N × the per-widget ceiling.
                if (n + 1 < entries.length && _nowMs() - walkStartedMs > ANIMATOR_FRAME_BUDGET_MS) {
                    _animatorResumeId = entries[(startAt + n + 1) % entries.length][0];
                    break;
                }
            }

            // The 1 Hz clock heartbeat, on this tick source rather than its own interval (D10).
            // Runs AFTER the animator renders and is awaited, so its write of the shared
            // triggerContext can never land inside one of them.
            const now = _nowMs();
            if (now - _clockBeatLastMs >= CLOCK_BEAT_INTERVAL_MS) {
                _clockBeatLastMs = now;
                try { await _renderConsumerPass(widgetConsumesClock, 'clock'); }
                catch (err) { console.warn('clock rerender failed:', err); }
            }
        } catch (e) {
            // The tick must survive anything the loop body itself can throw — a malformed
            // .phxlayer reaching getTriggerMeta, a torn-down canvas mid-shutdown. There is one
            // loop for the whole page now, and an uncaught throw here would reject the tick's
            // promise unhandled on EVERY frame; the finally below would keep rescheduling, so
            // the console noise would bury whatever actually broke.
            console.warn('[Visualist] animator tick failed:', e);
        } finally {
            _animatorTickInFlight = false;
            _ensureAnimatorLoop();
        }
    }

    /// `renderSeq` is the promoting render's position in render order (see _renderSeq). A render
    /// that STARTED before the slot currently in the map was installed may not replace it.
    function promoteWidgetAnimator(widget, trigger, renderSeq) {
        // Read AND clear both request flags first, unconditionally. Every early return below
        // depends on that: a flag left standing would leak into the next widget's render and
        // promote a widget that asked for nothing. renderWidgetTrigger's finally is the backstop
        // for the paths that never reach this line at all (a throwing render) and for overlapping
        // renders — it restores the value this render displaced. See the save/restore there.
        const wantsMedia = _animatorRequestMediaForCurrentRender;
        const wantsTime  = _animatorRequestTimeForCurrentRender;
        _animatorRequestMediaForCurrentRender = false;
        _animatorRequestTimeForCurrentRender  = false;

        // #6 — while the design-time Play loop owns this widget, IT is the
        // re-render driver (its rAF tick advances triggerContext.timeMs and
        // re-renders). Registering an animator slot here would re-create exactly
        // the competing free-running repaint the fix removes: renderWidgetTrigger
        // runs on every Play frame and would otherwise re-add the slot one frame
        // after handlePlay deleted it. handleStopPlay drops the slot so the GIF
        // holds its last frame.
        if (_playState && _playState.widget && _playState.widget.id === widget.id) return;
        // A dip-to-blank transition owns this widget right now — don't promote a
        // competing animator that would repaint live content over the fade. The
        // final render in renderWithTransition re-promotes once the dip completes.
        if (_widgetTransitions.has(widget.id)) return;

        const existing = _widgetAnimators.get(widget.id);

        // ★ STALE-PROMOTION REJECTION — a slot may only ever advance FORWARD in render order.
        //
        // The interleave this prevents, on ONE widget (full write-up at _renderSeq): the animator
        // loop starts rendering A's onStartup, raises the time request and suspends on a cold image
        // decode; a RUN_TRIGGER for A arrives and installs a slot for the ALERT trigger; the
        // suspended animator render then resumes — with its own request flag faithfully restored by
        // renderWidgetTrigger's finally — and lands here carrying the OLD trigger. Without this
        // check it replaces the fresh alert slot, and the loop then repaints the old graph over the
        // new content for the whole hold, on stream. With it, the older render simply loses.
        //
        // ONE comparison covers all three mutations below (demote-delete, media refresh, replace),
        // because all three are "this render decides what the slot is" and a superseded render is
        // entitled to decide nothing. It is inert on every ordinary path: renders that do not
        // overlap always arrive in increasing seq, and `>` (not `>=`) lets a render refresh the
        // slot it installed itself.
        //
        // A seeded slot carries seq 0 (_seedWidgetAnimator) precisely so it never blocks anything:
        // a seed is provisional and the first real render must be free to re-decide it.
        if (existing && existing.renderSeq > renderSeq) return;

        if (!wantsMedia && !wantsTime) {
            // Nothing animated on this trigger — demote to render-once. This is the arm that
            // makes the V5 latch symmetric: a widget whose last keyframe the author just
            // deleted stops raising the flag and drops out of the loop here.
            if (existing) _widgetAnimators.delete(widget.id);
            return;
        }

        if (existing && existing.trigger === trigger) {
            // Same trigger, already registered — leave the slot (its breaker state, and whether
            // its keyframe track has already played out) alone.
            //
            // One thing must still be refreshed: media can start being requested on a LATER
            // render than the promoting one — a Result.If branch that now resolves to the GIF
            // arm, or a Path attribute edited to a .gif. Once media has been seen the slot stops
            // being time-only forever (sticky in the safe direction), so the extent bound can
            // never stop a widget that has an unbounded source.
            if (wantsMedia && existing.timeOnly) {
                existing.timeOnly        = false;
                existing.settledAtExtent = false;
            }
            // Advance the slot's render-order stamp: this render is at least as new as whatever
            // installed it (the rejection above guarantees renderSeq >= existing.renderSeq), so the
            // guard keeps describing the NEWEST render that spoke for this slot rather than
            // freezing at the first one.
            existing.renderSeq = renderSeq;
            // …and re-capture the payload for the same reason. Re-firing the SAME trigger with
            // new args (the normal case for an alert widget: two raids in a row both render
            // "onTrigger:alert") lands in this branch, and a slot still carrying the first
            // activation's eventData would animate the second raid with the first raider's
            // name — and, after V7, with the first raid's clip.
            existing.activation = _captureActivationContext();
        } else {
            // Trigger changed (or first promotion): a fresh slot, which also resets the failure
            // count and the played-out mark. The loop's identity check retires the superseded
            // slot even if it is mid-render.
            _widgetAnimators.set(widget.id, {
                widget, trigger,
                // The payload THIS render ran under — see _captureActivationContext. Captured
                // here rather than read live by the loop, because by the time the loop renders,
                // the page singleton belongs to whichever widget/pass wrote it last.
                activation:      _captureActivationContext(),
                timeOnly:        !wantsMedia,
                suspended:       false,
                settledAtExtent: false,
                renderSeq,
                _consecutiveFailures: 0,
                _suspendLogged:       false,
            });
        }
        _ensureAnimatorLoop();
    }

    // ── Graph evaluator ──────────────────────────────────────────────────────

    class Evaluator {
        constructor(graph, frame, widgetId) {
            this.graph    = graph;
            // Which WIDGET this evaluation belongs to. Carried on the Evaluator rather than
            // read off a module singleton at use time because the only consumer is the audio
            // sink's per-widget activation generation, and that lookup happens AFTER awaits:
            // a module-level "widget currently rendering" would be whatever the last
            // interleaving render wrote (renderWidgetTrigger is re-entrant — see the animator
            // block comment on triggerContext), and picking the wrong widget's generation is
            // exactly the replay bug the per-widget scoping exists to remove. The Evaluator is
            // constructed once per render, so `this` cannot drift. Empty for a tooling /
            // legacy construction, which then simply shares one implicit bucket.
            this.widgetId = widgetId || '';
            // Bug #2 — the rect this widget renders INTO, in logical widget pixels
            // (same space the manipulator handles + the Display sink use). Lets
            // Text.Render rasterize at FRAME size instead of a tight bitmap, so the
            // text composes with every manipulation node (Image.Transform / Crop /
            // Mask / Blend …) in one consistent coordinate space. Null when an
            // Evaluator is built without a frame (legacy / tooling) — Text.Render
            // then falls back to a snug measured bitmap.
            this.frame    = (frame && frame.width > 0 && frame.height > 0) ? frame : null;
            this.memo     = new Map();
            this.visiting = new Set();
            // V13 A2 — the trace collector, or null when nothing is tracing THIS render. Declared
            // here (rather than only assigned by the one caller that wants it) so the object shape
            // is stable for every Evaluator the hot render path constructs, and so the null check
            // in evalNodeOutput is a plain property read on a known field. renderWidgetTrigger
            // assigns a Set only when the widget owes a DEBUG_WIDGET_NODE frame — see
            // _traceArmedWidgets for why that is per activation and design-time only.
            this.trace    = null;
            // NO missing-arg latch here on purpose. It used to live on the Evaluator and
            // was documented as "once per fire", but the Evaluator is instantiated per
            // renderWidgetTrigger call, so its real lifetime is one RENDER — and both
            // reporters run on every non-trigger render, so the diagnostic went out at
            // frame rate. The latch is now the page-scoped _reportedMissingArgs, matching
            // the other two eval-path reporters. (The C# mirror's _loggedMissingArgs
            // ThreadStatic keeps its per-Evaluate() lifetime: NodeEvaluator is design-time
            // only and pushes no frame to Hub, so per-call there costs nothing.)
            // Sprint 7 — every kernel canvas returned via `value.image` is
            // tracked here so renderWidgetTrigger's finally clause can return
            // them to canvasPool as a batch once the Display sink has painted.
            // Order doesn't matter for release; the array is cleared (not
            // reused) on release so a faulting kernel can't re-release a
            // canvas the caller has already recycled.
            this.escapeCanvases = [];
        }

        // Sprint 7 — pool-aware allocator for kernel results that ESCAPE
        // (i.e. are returned in `{ image: <canvas>, ... }` to a downstream
        // node). The canvas is added to escapeCanvases and released as a
        // batch in renderWidgetTrigger's finally, after the Display sink's
        // drawImage has copied pixels into the visible context.
        acquireEscape(w, h) {
            const c = canvasPool.acquire(w | 0, h | 0);
            this.escapeCanvases.push(c);
            return c;
        }

        releaseEscapes() {
            const list = this.escapeCanvases;
            this.escapeCanvases = [];
            for (const c of list) canvasPool.release(c);
        }

        async evalImageInto(sinkNode) {
            const inLink = this.findLinkTo(sinkNode.Id, 'Image');
            if (!inLink) return null;
            const v = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            // Already an image → use it as-is.
            if (v && v.image) return v;
            // Otherwise the upstream produced DATA (string / number / colour /
            // vector / bool) rather than an image — most of the palette does. Draw
            // that value so wiring a data node straight into Display shows the
            // value instead of a blank "load failed". Returns null only for a
            // genuinely empty value (empty string / null) → the diagnostic card.
            return this.coerceToImage(v);
        }

        /// Turn a non-image evaluator value into a drawable {image,width,height}
        /// so the Display sink (and any image-only consumer) can show it. Strings
        /// and numbers render as text; a {r,g,b[,a]} colour fills a swatch; a
        /// {x,y,...} vector renders as its components. null / undefined / empty
        /// string return null (nothing to show). An object that already carries an
        /// .image passes straight through.
        coerceToImage(value) {
            if (value == null) return null;
            if (typeof value === 'object' && value.image) return value;

            // Colour → solid swatch.
            if (typeof value === 'object'
                && typeof value.r === 'number'
                && typeof value.g === 'number'
                && typeof value.b === 'number') {
                const w = 240, h = 240;
                const off = (typeof OffscreenCanvas !== 'undefined')
                    ? new OffscreenCanvas(w, h)
                    : (() => { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; })();
                const octx = off.getContext('2d');
                octx.fillStyle = colorToCss(value);
                octx.fillRect(0, 0, w, h);
                return { image: off, width: w, height: h };
            }

            // Build a label for the remaining scalar / vector / string cases.
            let label;
            if (typeof value === 'object' && typeof value.x === 'number') {
                const parts = [value.x, value.y, value.z, value.w]
                    .filter(n => typeof n === 'number')
                    .map(n => Number.isInteger(n) ? String(n) : n.toFixed(2));
                label = `(${parts.join(', ')})`;
            } else if (typeof value === 'number') {
                label = Number.isFinite(value)
                    ? (Number.isInteger(value) ? String(value) : value.toFixed(3))
                    : String(value);
            } else if (typeof value === 'boolean') {
                label = value ? 'true' : 'false';
            } else {
                label = String(value);
            }
            if (!label) return null;
            return this.renderLabelToImage(label);
        }

        /// Render a single line of text to a tightly-sized canvas with the default
        /// overlay styling (white, centred). Used by coerceToImage so a bare data
        /// value is legible on Display without the author wiring a Text.Render. The
        /// Display fit-to-rect math then scales it into the widget rect like any
        /// image. Kept separate from evalTextRender (which honours the node's own
        /// Font/Color/Alignment sockets) so this default path stays dependency-free.
        renderLabelToImage(text, fontSize = 64, color = '#ffffff') {
            const font = `${fontSize}px Inter, "Segoe UI", sans-serif`;
            const probe = (typeof OffscreenCanvas !== 'undefined')
                ? new OffscreenCanvas(8, 8)
                : (() => { const c = document.createElement('canvas'); c.width = 8; c.height = 8; return c; })();
            const pctx = probe.getContext('2d');
            pctx.font = font;
            const w = Math.max(8, Math.ceil(pctx.measureText(text).width)) + 32;   // LOGICAL
            const h = Math.ceil(fontSize * 1.4) + 16;                              // LOGICAL
            // Supersample (same blur fix as evalTextRender): rasterize SS× dense,
            // report LOGICAL w/h so the Display sink down-samples a crisp bitmap
            // instead of upscaling a 1× one.
            // SS headroom covers BOTH the device pixel ratio AND the Display
            // sink's fit-to-rect UPSCALE — text authored at e.g. 50px is shown
            // filling a much larger widget rect, which is the dominant blur. A
            // ~6x factor lets a 50px font fill a ~400px widget by DOWN-sampling
            // (sharp) rather than upscaling. Capped at 8, and clamped so the
            // offscreen long edge stays <= ~4096px on a long string.
            let SS = Math.min(8, Math.max(4, Math.ceil((window.devicePixelRatio || 1) * 6)));
            SS = Math.max(2, Math.min(SS, Math.floor(4096 / Math.max(w, h))));
            const off = (typeof OffscreenCanvas !== 'undefined')
                ? new OffscreenCanvas(w * SS, h * SS)
                : (() => { const c = document.createElement('canvas'); c.width = w * SS; c.height = h * SS; return c; })();
            const octx = off.getContext('2d');
            octx.scale(SS, SS);
            octx.clearRect(0, 0, w, h);
            octx.font         = font;
            octx.fillStyle    = color;
            octx.textBaseline = 'middle';
            octx.textAlign    = 'center';
            octx.fillText(text, w / 2, h / 2);
            return { image: off, width: w, height: h };
        }

        /// Walks every input socket of the given node and resolves whichever ones are linked.
        /// Used by Visual.Complete which is a generic "did the chain settle" probe.
        async evalAnyInputOf(node) {
            const links = this.graph.Links.filter(l => l.ToNodeId === node.Id);
            for (const link of links) {
                try { await this.evalNodeOutput(link.FromNodeId, link.FromSocketId); }
                catch (e) { console.warn('Visual.Complete upstream eval failed:', e); }
            }
        }

        /// V13 H1 — resolves Visual.Complete's `Payload` input, or NULL when that pin is not
        /// WIRED. Null is the signal to OMIT the field from the VISUAL_COMPLETE frame, and that
        /// omission is the sprint's compatibility gate: an unwired pin must put byte-identical
        /// bytes on the wire and leave every exporter golden byte-identical.
        ///
        /// The test is a LINK test, deliberately not a value test, and it is also what makes this
        /// half inert until the C# template grows the socket: findLinkTo resolves the socket by
        /// name on the node itself, so against today's one-input Visual.Complete it returns null
        /// and nothing changes for anybody.
        ///
        /// The value comes from _evalStringSocket — the same resolver every other wirable String
        /// input in this file reads through (String.Concat / Upper / Lower / Slice / Replace, the
        /// WebOverlay.Custom slots) — and NOT from its sibling _evalQuotedStringSocket: that one
        /// exists to strip the JSON quoting the Inspector puts on an ATTRIBUTE, and this pin's
        /// attribute is never consulted, because an unwired pin is omitted by contract.
        async resolveCompletionPayload(node) {
            if (!node) return null;
            if (!this.findLinkTo(node.Id, 'Payload')) return null;
            try { return await this._evalStringSocket(node, 'Payload', ''); }
            catch (e) {
                // The chain was already walked (and its throw already logged) by evalAnyInputOf,
                // so this only fires for a repeat throw. A payload we cannot resolve degrades to
                // "no payload" rather than failing the completion: dropping the ack itself would
                // hang the waiting script until wait_for_visual's full timeout.
                console.warn('[Visualist] Visual.Complete Payload resolve failed:', e);
                return null;
            }
        }

        async evalNodeOutput(nodeId, socketId) {
            if (this.visiting.has(nodeId)) {
                // M68 — cycle detection. C# NodeEvaluator returns
                // { HasError = true, ErrorMessage = "cycle detected at node ..." };
                // historically the JS side returned `null` + console.warn, which
                // meant the user saw a silently empty widget while tests asserted
                // a hard error. Both sides now error, with the JS side surfacing
                // a visible "ERR" placeholder image so the broken state is obvious
                // to whoever is staring at the OBS preview.
                console.warn('[Visualist] cycle in graph at', nodeId);
                return makeErrorPlaceholder(`cycle at ${nodeId}`);
            }
            const memoKey = `${nodeId}.${socketId}`;
            if (this.memo.has(memoKey)) return this.memo.get(memoKey);

            const node = this.graph.Nodes.find(n => n.Id === nodeId);
            if (!node) return null;

            this.visiting.add(nodeId);
            // V13 A2 — record the visit. Placed right after the visiting mark, which puts it
            // AFTER the memo hit above and after the unknown-node bail: a diamond node is
            // therefore listed once rather than once per downstream consumer, and a dangling
            // link contributes nothing. That is exactly where the C# mirror records it
            // (NodeEvaluator.EvalImage's `visited.Add(nodeId)` follows `visiting.Add(nodeId)`),
            // so the two sides list the same nodes for the same graph.
            if (this.trace) this.trace.add(nodeId);
            let value;
            switch (node.Title) {
                case 'Image.Load':          value = await this.evalImageLoad(node);    break;
                case 'Image.LoadUrl':       value = await this.evalImageLoadUrl(node); break;
                case 'Video.Load':          value = await this.evalVideoLoad(node, socketId); break;
                case 'Audio.Load':          value = await this.evalAudioLoad(node);    break;
                case 'Audio.Play':          value = await this.evalAudioPlay(node);    break;
                case 'Image.Scale':         value = await this.evalImageScale(node);   break;
                case 'Color.Constant':      value = this.evalColorConstant(node);      break;
                case 'Scalar.Constant':     value = this.evalScalarConstant(node);     break;
                case 'String.Constant':     value = this.evalStringConstant(node);     break;
                case 'Vector2.Constant':    value = this.evalVector2Constant(node);    break;
                // M56 / F12 — Vector3 / Vector4 constant producers + Vector.Rect4 alias.
                // Mirrors Vector2.Constant's attribute-driven shape so authors can wire
                // typed vector inputs (LerpVector{3,4}, Image.Crop's Rect:Vector4) without
                // an external producer. C# templates declared in NodeTemplates.cs.
                case 'Vector3.Constant':    value = this.evalVector3Constant(node);    break;
                case 'Vector4.Constant':    value = this.evalVector4Constant(node);    break;
                case 'Vector.Rect4':        value = this.evalVectorRect4(node);        break;
                // Overlay Live Channel readers. Every one of these resolves its value out of
                // liveState — the keys it declared in liveKeysForNode and nothing else.
                case 'Caption.LiveCaption': value = this.evalCaptionLive(node, socketId); break;
                case 'Timer.Remaining':     value = this.evalTimerRemaining(node, socketId); break;
                // Countdown.Remaining / Stopwatch.Elapsed read the SAME timer.<root>.* key
                // family as Timer.Remaining — Hub publishes the mode-aware display value
                // (elapsed for a stopwatch, remaining for a countdown) in the short/long/clock
                // fields, so the reader is shared. Clock.Now is the odd one out: browser-
                // autonomous, no key, no producer — it reads the OBS machine's own wall clock.
                case 'Countdown.Remaining': value = this.evalTimerRemaining(node, socketId); break;
                case 'Stopwatch.Elapsed':   value = this.evalTimerRemaining(node, socketId); break;
                case 'Clock.Now':           value = this.evalClockNow(node); break;
                case 'Loyalty.Leaderboard': value = this.evalLoyaltyLeaderboard(node, socketId); break;
                case 'Loyalty.Balance':     value = this.evalLoyaltyBalance(node, socketId);     break;
                case 'Counter.Value':       value = this.evalCounterValue(node, socketId);       break;
                // The author-facing binding node — any channel key by literal name, tool-owned
                // or overlay.publish'd, with the type coercion done at the pin the author chose.
                case 'Var.Live':            value = this.evalVarLive(node, socketId);            break;
                // V10 — the two family readers. Goal.Progress reads the reserved goal.<kind>.*
                // root; List.Live reads any key holding a JSON array. Both are generic rather
                // than per-tool: a Stat.LatestFollower or a TipJar.Total would be exactly the
                // per-tool special-casing the channel exists to abolish.
                case 'Goal.Progress':       value = this.evalGoalProgress(node, socketId);       break;
                case 'List.Live':           value = await this.evalListLive(node, socketId);     break;
                case 'Text.Translate':      value = await this.evalTextTranslate(node); break;
                case 'Text.Render':         value = await this.evalTextRender(node);  break;

                // F5 / H61 — Visual.OnStartup / OnTrigger as event-data sources.
                case 'Visual.OnStartup':    value = this.evalVisualOnStartup(node, socketId); break;
                case 'Visual.OnTrigger':    value = this.evalVisualOnTrigger(node, socketId); break;

                // C10 / F7 — Math kernels.
                case 'Math.Add':            value = await this.evalMathBinary(node, (a, b) => a + b); break;
                case 'Math.Sub':            value = await this.evalMathBinary(node, (a, b) => a - b); break;
                case 'Math.Mul':            value = await this.evalMathBinary(node, (a, b) => a * b); break;
                case 'Math.Div':            value = await this.evalMathBinary(node, (a, b) => b === 0 ? 0 : a / b); break;
                case 'Math.Lerp':           value = await this.evalMathLerp(node);  break;
                // M56 / F12 — Math.LerpVector{2,3,4} mirror the scalar Math.Lerp pattern
                // but operate component-wise on Vector2/3/4 inputs. T is always Scalar.
                case 'Math.LerpVector2':    value = await this.evalMathLerpVectorN(node, 2); break;
                case 'Math.LerpVector3':    value = await this.evalMathLerpVectorN(node, 3); break;
                case 'Math.LerpVector4':    value = await this.evalMathLerpVectorN(node, 4); break;
                case 'Math.Clamp':          value = await this.evalMathClamp(node); break;
                case 'Math.Resolution':     value = this.evalMathResolution();      break;

                // C10 / F7 — Vector kernels. Vector2 versions use the legacy
                // "Vector.Split / Vector.Combine" titles; M56 / F12 added the
                // dimensionality-suffixed variants for Vector3 / Vector4 so the
                // catalog stays unambiguous.
                case 'Vector.Split':        value = await this.evalVectorSplit(node, socketId); break;
                case 'Vector.Combine':      value = await this.evalVectorCombine(node);         break;
                case 'Vector3.Split':       value = await this.evalVectorNSplit(node, socketId, 3); break;
                case 'Vector4.Split':       value = await this.evalVectorNSplit(node, socketId, 4); break;
                case 'Vector3.Combine':     value = await this.evalVectorNCombine(node, 3);     break;
                case 'Vector4.Combine':     value = await this.evalVectorNCombine(node, 4);     break;

                // H65 / F14 — Viewer passthrough.
                case 'Viewer':              value = await this.evalViewer(node); break;

                // C10 / F7 — Image.Crop has a simple canvas implementation that doesn't
                // need shader work. Crop a Vector4 region (x,y,w,h, all in 0..1 fractions
                // of the source-image dimensions — [QC50-08] canonical convention shared
                // with NodeTemplates.cs and EvalImageCrop in NodeEvaluator.cs) into a
                // fresh canvas; downstream nodes treat it as an Image.
                case 'Image.Crop': {
                    const inLink = this.findLinkTo(node.Id, 'In');
                    if (!inLink) { value = null; break; }
                    const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
                    if (!upstream || !upstream.image) { value = upstream; break; }
                    // Read Rect from socket (Vector4) or Attribute fallback "x,y,w,h".
                    let rect = await this.resolveVector4Socket(node, 'Rect');
                    if (!rect) {
                        const raw = (node.Attributes && (node.Attributes.Rect || node.Attributes.rect)) || '0,0,1,1';
                        const p = String(raw).split(',').map(parseFloat);
                        rect = { x: p[0]||0, y: p[1]||0, z: p[2]||1, w: p[3]||1 };
                    }
                    const sw = upstream.width  || upstream.image.width;
                    const sh = upstream.height || upstream.image.height;
                    const sx = Math.max(0, Math.min(sw, rect.x * sw));
                    const sy = Math.max(0, Math.min(sh, rect.y * sh));
                    const cw = Math.max(1, Math.min(sw - sx, rect.z * sw));
                    const ch = Math.max(1, Math.min(sh - sy, rect.w * sh));
                    const off = this.acquireEscape(cw, ch);
                    off.getContext('2d').drawImage(upstream.image, sx, sy, cw, ch, 0, 0, cw, ch);
                    value = { image: off, width: cw, height: ch };
                    break;
                }

                // C10 / F7 follow-up — Image.Transform (translate/scale/rotate) also
                // implementable via canvas 2D context without a shader. The original
                // image is composited onto an off-screen canvas with the requested
                // transform applied.
                case 'Image.Transform': {
                    const inLink = this.findLinkTo(node.Id, 'In');
                    if (!inLink) { value = null; break; }
                    const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
                    if (!upstream || !upstream.image) { value = upstream; break; }
                    // Sweep 21 — animated-attr read so SCRUB/PLAY scrubs these values
                    // at design time. Production renders sample at timeMs=0 which equals
                    // the static attribute for un-keyframed parameters.
                    const tx = parseFloat(attrAnimated(node, 'TranslateX', '0')) || 0;
                    const ty = parseFloat(attrAnimated(node, 'TranslateY', '0')) || 0;
                    // FALSY-ZERO FIX — ScaleX/ScaleY are canonical animation targets, so a
                    // keyframed 0 (the natural first frame of a scale-in) is legitimate and
                    // `|| 1` snapped it to full size. TranslateX/Y/Rotation default to 0, so
                    // their `|| 0` is a no-op on a real 0 and stays as-is.
                    const _sxRaw = parseFloat(attrAnimated(node, 'ScaleX', '1'));
                    const _syRaw = parseFloat(attrAnimated(node, 'ScaleY', '1'));
                    const sx = Number.isFinite(_sxRaw) ? _sxRaw : 1;
                    const sy = Number.isFinite(_syRaw) ? _syRaw : 1;
                    const rot = parseFloat(attrAnimated(node, 'Rotation', '0')) || 0;
                    const w = upstream.width  || upstream.image.width;
                    const h = upstream.height || upstream.image.height;
                    // Bug #1 (text blurry) — PRESERVE the upstream's pixel density
                    // through the transform. Text.Render rasterizes at a denser-than-
                    // logical backing so it stays sharp when the small widget-rect
                    // preview canvas is shown enlarged; if the transform flattened it to
                    // logical here (the old 3-arg drawImage also drew the dense bitmap at
                    // its PHYSICAL size → clipped), the text went soft (or huge). dens =
                    // physical/logical of the source (1 for a normal image, 2-3 for
                    // supersampled text). Back the offscreen at logical×dens, pre-scale the
                    // ctx so all transform math stays in logical widget coords, and draw
                    // the FULL physical source into the logical rect (9-arg) so density is
                    // carried, not lost. Report LOGICAL w/h so Display + downstream stay
                    // logical. dens===1 is byte-identical to the old correct path.
                    const pw = upstream.image.width, ph = upstream.image.height;
                    const dens = Math.max(1, Math.min(4, Math.round(pw / Math.max(1, w))));
                    // ── Crop ONLY on export (Majo, 0.13.6) ──────────────────────────
                    // Size the offscreen to CONTAIN the transformed content, NOT clip it
                    // to the widget frame. A chained Transform / Blend downstream must
                    // receive the FULL content — anything pushed past the widget edge
                    // here would otherwise be destroyed before the next node can pull it
                    // back ("the last Image.Transform gets a cropped input from the
                    // previous transform"). The widget crop happens once, at Display.
                    //
                    // The canvas stays SYMMETRIC about the widget centre (its centre IS
                    // the widget centre, offset 0), so every image in the pipeline shares
                    // one anchor — Blend/Combine/Mask centre-align onto it and Display
                    // centre-draws + clips. Half-extent per axis = max(widget half,
                    // |translate| + rotated·scaled content half), so the canvas always
                    // covers BOTH the widget window (Display reads it) and all content.
                    const fr = this.frame || { width: w, height: h };
                    const fw = Math.max(1, fr.width), fh = Math.max(1, fr.height);
                    const rad = rot * Math.PI / 180;
                    const ac = Math.abs(Math.cos(rad)), as = Math.abs(Math.sin(rad));
                    const chw = (w / 2) * Math.abs(sx), chh = (h / 2) * Math.abs(sy);
                    const ehw = ac * chw + as * chh;   // rotated AABB half-width
                    const ehh = as * chw + ac * chh;   // rotated AABB half-height
                    const ow = Math.max(1, Math.ceil(Math.max(fw / 2, Math.abs(tx) + ehw) * 2));
                    const oh = Math.max(1, Math.ceil(Math.max(fh / 2, Math.abs(ty) + ehh) * 2));
                    const off = this.acquireEscape(Math.round(ow * dens), Math.round(oh * dens));
                    const octx = off.getContext('2d');
                    octx.scale(dens, dens);
                    // Canvas centre == widget centre; translate in widget px from there.
                    octx.translate(ow / 2 + tx, oh / 2 + ty);
                    octx.rotate(rad);
                    octx.scale(sx, sy);
                    octx.drawImage(upstream.image, 0, 0, pw, ph, -w / 2, -h / 2, w, h);
                    // Content-sized output (centred on the widget) → Display crops it.
                    value = { image: off, width: ow, height: oh };
                    break;
                }

                // C10 / F7 — Canvas2D implementations of the remaining four
                // image kernels. Per-pixel shader work isn't available without
                // WebGL, but every operation here can be expressed via either
                // ctx.filter (ColorAdjust), globalCompositeOperation
                // (Mask/Blend), or createPattern (Tile).
                //
                // Error policy: when a required input is missing we return null
                // and log a console.warn — same policy used by Image.Crop's
                // missing-input case. The C# mirror surfaces HasError=true on
                // the same conditions so tests can pin the contract.
                case 'Image.ColorAdjust': value = await this.evalImageColorAdjust(node); break;
                case 'Image.Blur':        value = await this.evalImageBlur(node);        break;
                case 'Image.Gaussian':    value = await this.evalImageGaussian(node);    break;
                case 'Image.Mosaic':      value = await this.evalImageMosaic(node);      break;
                case 'Image.Shadow':      value = await this.evalImageShadow(node);      break;
                case 'Image.Glow':        value = await this.evalImageGlow(node);        break;
                case 'Image.Distort':     value = await this.evalImageDistort(node);     break;
                case 'Image.Mask':        value = await this.evalImageMask(node);        break;
                // Sweep 22 — procedural mask shape generators. All emit an Image at the
                // layer's resolution; downstream Image.Mask consumes them as the mask
                // channel. Parameters are scalar (animated via the sweep-21 pipeline).
                case 'Mask.Rectangle':       value = this.evalMaskRectangle(node);       break;
                case 'Mask.Circle':          value = this.evalMaskCircle(node);          break;
                case 'Mask.Ellipse':         value = this.evalMaskEllipse(node);         break;
                case 'Mask.LinearGradient':  value = this.evalMaskLinearGradient(node);  break;
                case 'Mask.RadialGradient':  value = this.evalMaskRadialGradient(node);  break;
                case 'Mask.Vignette':        value = this.evalMaskVignette(node);        break;
                // Sweep 23 — vertex-list (Polygon/Bezier) + parameterised (Star) shapes.
                case 'Mask.Polygon':         value = this.evalMaskPolygon(node);         break;
                case 'Mask.Bezier':          value = this.evalMaskBezier(node);          break;
                case 'Mask.Star':            value = this.evalMaskStar(node);            break;
                // V10 — Image.Solid: the palette's only COLOURED, WIRABLE fill. The Mask.*
                // family above emits white-on-transparent from attribute-only geometry, so
                // nothing in the catalog could tint a shape or let a live value drive one.
                // Note the geometry space differs from the masks on purpose: their fractions
                // are of the LAYER, these are of the WIDGET FRAME (see evalImageSolid).
                case 'Image.Solid':          value = await this.evalImageSolid(node);    break;
                case 'Image.Blend':       value = await this.evalImageBlend(node);       break;
                case 'Image.Combine':     value = await this.evalImageCombine(node);     break;
                case 'Image.Tile':        value = await this.evalImageTile(node);        break;

                // Result.If — gate the upstream In image based on a comparison between
                // triggerContext.eventData[When] (e.g. "Args1") and the Equals attribute
                // (or wired Equals input). On match → pass In through. On mismatch or
                // when the named arg is missing → emit null so Display sees nothing
                // flowing into this branch. Missing-arg diagnostic via
                // sendEvalDiagnostic, deduped once per (node, arg, kind) per page on
                // _reportedMissingArgs.
                case 'Result.If':         value = await this.evalResultIf(node);         break;

                // Sprint 91 — WebSource runtime: fetch + image-content-type
                // only. If the URL returns image/* the proxied bytes flow
                // through Image.LoadUrl's same loadImage path (browser-cached
                // + LRU-evicted). HTML pages are NOT supported in this slice
                // — UrlImageCache rejects non-image MIME types at validation
                // and the loadImage promise rejects, surfacing a clear
                // WebSource-specific console.warn.
                case 'WebSource':         value = await this.evalWebSource(node);    break;
                // Sprint 92 — Particles.Emit rAF-driven runtime. Tick-based
                // emitter, sprite render; requestWidgetAnimator hooks the widget
                // into the global animator loop so particles flow between
                // triggers.
                case 'Particles.Emit':    value = this.evalParticlesEmit(node);      break;

                // Track D — numeric Math kernels. Scalar attrs read via attrAnimated
                // (keyframeable); wired inputs override the inline attribute through
                // _evalAnimScalarSocket. Pure-data: no Flow socket.
                case 'Math.Mod':          value = await this.evalMathMod(node);       break;
                case 'Math.Pow':          value = await this.evalMathPow(node);       break;
                case 'Math.Min':          value = await this.evalMathMin(node);       break;
                case 'Math.Max':          value = await this.evalMathMax(node);       break;
                case 'Math.Abs':          value = await this.evalMathUnary(node, 'V', Math.abs); break;
                case 'Math.Sqrt':         value = await this.evalMathUnary(node, 'V', v => v < 0 ? 0 : Math.sqrt(v)); break;
                case 'Math.Floor':        value = await this.evalMathUnary(node, 'V', Math.floor); break;
                case 'Math.Ceil':         value = await this.evalMathUnary(node, 'V', Math.ceil);  break;
                case 'Math.Round':        value = await this.evalMathUnary(node, 'V', Math.round); break;
                case 'Math.Sign':         value = await this.evalMathUnary(node, 'V', Math.sign);  break;
                case 'Math.Negate':       value = await this.evalMathUnary(node, 'V', v => -v);    break;
                case 'Math.Sin':          value = await this.evalMathUnary(node, 'Degrees', d => Math.sin(d * Math.PI / 180)); break;
                case 'Math.Cos':          value = await this.evalMathUnary(node, 'Degrees', d => Math.cos(d * Math.PI / 180)); break;
                case 'Math.Tan':          value = await this.evalMathUnary(node, 'Degrees', d => Math.tan(d * Math.PI / 180)); break;
                case 'Math.Remap':        value = await this.evalMathRemap(node);     break;
                case 'Math.Compare':      value = await this.evalMathCompare(node);   break;

                // Track D — Time / animation nodes. timeMs (ms) → seconds; production
                // renders (no SCRUB/PLAY) sample at timeMs=0, matching the C# t=0 mirror.
                case 'Time.Elapsed':      value = this.evalTimeElapsed();             break;
                case 'Time.Oscillator':   value = await this.evalTimeOscillator(node); break;
                case 'Time.Sawtooth':     value = await this.evalTimeSawtooth(node);  break;
                case 'Time.Easing':       value = await this.evalTimeEasing(node);    break;

                // Track D — String nodes. String/enum attrs use plain attr(); wired
                // String inputs override via _evalStringSocket.
                case 'String.Concat':     value = await this.evalStringConcat(node);  break;
                case 'String.Upper':      value = await this.evalStringUpper(node);   break;
                case 'String.Lower':      value = await this.evalStringLower(node);   break;
                case 'String.Length':     value = await this.evalStringLength(node);  break;
                case 'String.Slice':      value = await this.evalStringSlice(node);   break;
                case 'String.Replace':    value = await this.evalStringReplace(node); break;
                // V7 — the per-kind mapping node. See evalStringSelect for the matching
                // rules and why the Default row is mandatory.
                case 'String.Select':     value = await this.evalStringSelect(node);  break;

                // Track D — Convert nodes (scalar↔string, RGBA→Color, hex→Color).
                case 'Convert.NumberToString': value = await this.evalConvertNumberToString(node); break;
                case 'Convert.StringToNumber': value = await this.evalConvertStringToNumber(node); break;
                case 'Convert.ColorFromRGBA':  value = await this.evalConvertColorFromRGBA(node);  break;
                case 'Convert.HexToColor':     value = await this.evalConvertHexToColor(node);     break;

                // Track D — Message.Read: the read-out node for the transmitted
                // message. Reads triggerContext.eventData[Key] with MockValue fallback.
                case 'Message.Read':      value = this.evalMessageRead(node);         break;
                // V7 — Visual.Arg: the same read, but its placeholder is design-time only,
                // so an unsupplied field renders nothing on stream. Prefer it in new graphs.
                case 'Visual.Arg':        value = this.evalVisualArg(node);           break;

                default:
                    console.warn(`compositor: unsupported node '${node.Title}' — returning null.`);
                    value = null;
            }
            this.visiting.delete(nodeId);
            this.memo.set(memoKey, value);
            return value;
        }

        findLinkTo(nodeId, socketName) {
            const node = this.graph.Nodes.find(n => n.Id === nodeId);
            if (!node) return null;
            const sock = (node.Sockets || []).find(s => s.Name === socketName && s.Type === 0); // 0 = Input
            if (!sock) return null;
            return this.graph.Links.find(l => l.ToNodeId === nodeId && l.ToSocketId === sock.Id);
        }

        // V7 — Path is a wirable String input with the attribute as its fallback, so the
        // file can be chosen at trigger time. Everything downstream is unchanged: the GIF
        // sniff still reads `path`, and both returns still go through
        // fitLoadedImageToFrame, so a dynamic source is contain-fitted exactly like a
        // typed one.
        //
        // Reads through _evalMediaPathSocket, which is the provenance guard: a WIRED path
        // must be relative (rejected values return '' and land in the empty-path bail
        // below), an attribute path behaves exactly as before.
        async evalImageLoad(node) {
            const path = await this._evalMediaPathSocket(node);
            if (!path) {
                debugLog('evalImageLoad.empty-path', { nodeId: node.Id });
                return null;
            }
            const url = resolveMediaPath(path);
            try {
                const img = await loadImage(url);
                if (/\.gif(\?|$)/i.test(path)) {
                    // Animated GIF: decode frames (WebCodecs ImageDecoder) and sample
                    // the current one, re-rendering continuously so the canvas advances.
                    // ctx.drawImage of an <img> only ever captures frame 0 (Chromium
                    // won't animate an off-DOM / opacity:0 image), so the canvas froze
                    // even though the WinUI node-body preview animated. currentGifFrame
                    // is null until the decode completes (or for a single-frame / older
                    // runtime), so we fall back to the static <img> — plus the legacy
                    // DOM-pump when WebCodecs is unavailable — until a frame is ready.
                    requestWidgetAnimator();
                    const frame = currentGifFrame(url);
                    if (frame) {
                        const gfit = fitLoadedImageToFrame(frame.width, frame.height, this.frame);
                        debugLog('evalImageLoad.gif', { nodeId: node.Id, url, w: gfit.width, h: gfit.height });
                        return { image: frame, width: gfit.width, height: gfit.height };
                    }
                    if (!_hasImageDecoder()) ensureGifAnimating(img);
                }
                const fit = fitLoadedImageToFrame(img.width, img.height, this.frame);
                debugLog('evalImageLoad.ok', { nodeId: node.Id, url, w: fit.width, h: fit.height });
                return { image: img, width: fit.width, height: fit.height };
            } catch (e) {
                console.warn(`evalImageLoad failed for "${url}": ${e.message}`);
                debugLog('evalImageLoad.fail', { nodeId: node.Id, url, error: e.message });
                return null;
            }
        }

        async evalImageLoadUrl(node) {
            const url = stripQuotes(attr(node, 'Url', ''));
            if (!url) return null;
            try {
                const proxied = `/asset/url?u=${encodeURIComponent(url)}`;
                const img = await loadImage(proxied);
                if (/\.gif(\?|$)/i.test(url)) {
                    requestWidgetAnimator();
                    const frame = currentGifFrame(proxied);
                    if (frame) {
                        const gfit = fitLoadedImageToFrame(frame.width, frame.height, this.frame);
                        return { image: frame, width: gfit.width, height: gfit.height };
                    }
                    if (!_hasImageDecoder()) ensureGifAnimating(img);
                }
                const fit = fitLoadedImageToFrame(img.width, img.height, this.frame);
                return { image: img, width: fit.width, height: fit.height };
            } catch (e) { console.warn(e.message); return null; }
        }

        // Sprint 91 — WebSource runtime. Goes through the same Hub-side
        // /asset/url proxy as Image.LoadUrl (UrlImageCache validates SSRF +
        // MIME allowlist, then caches the image bytes locally), then loads
        // the proxied bytes as an HTMLImageElement. RefreshSeconds is the
        // node-level TTL knob: a cache-busting `_ts` bucket query param
        // changes value every RefreshSeconds, which forces loadImage's
        // LRU cache to miss and re-fetch. Within a bucket window the same
        // image returns for repeated triggers — same shape Image.LoadUrl
        // gets implicitly via the browser-side cache.
        //
        // The WebSource decision (sprint 90 / A1c) is "fetch + image-content-
        // type only". HTML pages and arbitrary embedded content are out of
        // scope for the first slice — UrlImageCache rejects non-image MIME
        // types at validation, so the loadImage promise rejects with a
        // network error and we surface a clear WebSource-specific
        // console.warn so authors see "the URL didn't return an image"
        // rather than the generic "image load failed" message.
        async evalWebSource(node) {
            const url = stripQuotes(attr(node, 'Url', ''));
            if (!url) return null;
            const refreshSeconds = Math.max(0, parseFloat(attr(node, 'RefreshSeconds', '5')) || 0);
            // Bucket math — when RefreshSeconds is 0, all triggers share
            // bucket=0 and the browser cache holds the image indefinitely.
            // When RefreshSeconds > 0, the bucket flips every N seconds so
            // cross-bucket triggers refetch. Math.floor(now / windowMs) is
            // monotonic so previous buckets stay invalid even if the system
            // clock jumps backward by less than one window.
            const bucket = refreshSeconds > 0
                ? Math.floor(Date.now() / (refreshSeconds * 1000))
                : 0;
            const proxied = `/asset/url?u=${encodeURIComponent(url)}` +
                            (refreshSeconds > 0 ? `&_ts=${bucket}` : '');
            try {
                const img = await loadImage(proxied);
                const fit = fitLoadedImageToFrame(img.width, img.height, this.frame);
                return { image: img, width: fit.width, height: fit.height };
            } catch (e) {
                console.warn(
                    `[Visualist] WebSource: ${url} did not return an image (${e.message}). ` +
                    `WebSource only renders image URLs (image/png, image/jpeg, image/gif, image/webp). ` +
                    `HTML pages are not supported in this slice — embed an image-only feed (e.g. an OBS overlay screenshot endpoint) or use Image.LoadUrl for static URLs.`);
                return null;
            }
        }

        // Sprint 92 — Particles.Emit runtime. Tick-based 2D-sprite emitter.
        // Per-node state in _particleState carries the active particle list +
        // a lastTickMs timestamp; each evaluation advances state by the wall-
        // clock delta since the previous tick, emits Rate * dt new particles
        // (with a small uniform fractional carry for sub-frame rates so a
        // Rate=0.5 still drips one particle every two seconds), and renders
        // each surviving particle as a filled circle on a fresh canvas at
        // the layer resolution. requestWidgetAnimator() opts the widget into
        // the global animator loop so the state ticks between author triggers
        // — the decision A2b rAF-opt-in model, now with ONE loop for the whole
        // page instead of one per widget (see promoteWidgetAnimator). Particles
        // already cap at PARTICLE_HARD_CAP per node (500) to bound RAM.
        //
        // Position / Velocity are unit-space (0..1 in each axis), so the
        // same authored .phxlayer renders identically across resolutions.
        // Velocity gets a small symmetric jitter (±20% of the magnitude)
        // applied at emit so a constant authored Velocity still produces
        // a natural-looking spread instead of a single ballistic line.
        // Color is a CSS color literal; alpha is multiplied by the
        // particle's remaining life-fraction so they fade as they age.
        evalParticlesEmit(node) {
            const state = _particleState.get(node.Id) ?? { particles: [], lastTickMs: 0, emitCarry: 0 };
            const now = (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
            // First tick gets dt=0; clamp at 0.25s so a tab that was backgrounded
            // for a long time doesn't spike-emit a thousand particles on resume.
            const dt = state.lastTickMs ? Math.min(0.25, (now - state.lastTickMs) / 1000) : 0;
            state.lastTickMs = now;

            // QC50-12 — Position / Velocity now persist as per-component scalar
            // attributes (PositionX/PositionY, VelocityX/VelocityY) matching
            // the convention used elsewhere in the Visualist catalog. The
            // legacy comma-CSV form is migrated on load by C# LayerGraphMigrator;
            // we keep the CSV-aware parser here as a belt-and-braces fallback for
            // any layer that bypassed LayerDocument.Open (e.g. a third-party tool
            // hand-editing .phxlayer files in place).
            const parseV2 = (raw, fallbackX, fallbackY) => {
                const parts = String(raw || '').split(',').map(s => parseFloat(s.trim()));
                const x = Number.isFinite(parts[0]) ? parts[0] : fallbackX;
                const y = Number.isFinite(parts[1]) ? parts[1] : fallbackY;
                return [x, y];
            };
            const readV2 = (xKey, yKey, legacyKey, fallbackX, fallbackY) => {
                // Prefer canonical per-component scalars; both halves must be
                // present on the node otherwise we fall back to the legacy CSV.
                const hasNew = (node.Attributes && (xKey in node.Attributes) && (yKey in node.Attributes));
                if (hasNew) {
                    const x = parseFloat(attr(node, xKey, String(fallbackX)));
                    const y = parseFloat(attr(node, yKey, String(fallbackY)));
                    return [Number.isFinite(x) ? x : fallbackX, Number.isFinite(y) ? y : fallbackY];
                }
                return parseV2(stripQuotes(attr(node, legacyKey, `${fallbackX}, ${fallbackY}`)), fallbackX, fallbackY);
            };
            const [px, py] = readV2('PositionX', 'PositionY', 'Position', 0.5, 0.5);
            const [vx, vy] = readV2('VelocityX', 'VelocityY', 'Velocity', 0, -0.2);
            const lifetime = Math.max(0.01, parseFloat(attr(node, 'Lifetime', '1.5')) || 1.5);
            const rate     = Math.max(0,    parseFloat(attr(node, 'Rate',     '20'))  || 0);
            const color    = attrAnimatedColor(node, 'Color', '#ffffffff');

            // Advance existing particles. Iterate via filter so dead ones
            // (age >= lifetime) drop out cleanly.
            const survivors = [];
            for (let i = 0; i < state.particles.length; i++) {
                const p = state.particles[i];
                p.age += dt;
                if (p.age >= p.lifetime) continue;
                p.x += p.vx * dt;
                p.y += p.vy * dt;
                survivors.push(p);
            }
            state.particles = survivors;

            // Emit new particles. emitCarry holds the sub-particle fraction
            // so a Rate slower than 1/frame still emits eventually.
            state.emitCarry += rate * dt;
            let toEmit = Math.floor(state.emitCarry);
            state.emitCarry -= toEmit;
            // Hard cap — never let a runaway Rate accumulate beyond the cap.
            if (state.particles.length + toEmit > PARTICLE_HARD_CAP) {
                toEmit = Math.max(0, PARTICLE_HARD_CAP - state.particles.length);
            }
            for (let i = 0; i < toEmit; i++) {
                // ±20% velocity jitter so a constant authored Velocity still
                // looks like a natural spread instead of a single line.
                const jx = vx * (0.8 + Math.random() * 0.4);
                const jy = vy * (0.8 + Math.random() * 0.4);
                state.particles.push({
                    x: px,
                    y: py,
                    vx: jx,
                    vy: jy,
                    age: 0,
                    lifetime: lifetime,
                    color: color,
                });
            }

            _particleState.set(node.Id, state);
            // Tell the widget animator to run another rAF tick — without this
            // the engine evaluates the graph only on author-driven triggers
            // and the particles never advance between events.
            requestWidgetAnimator();

            // Render to a layer-resolution canvas. unit-space (x, y) maps to
            // (x*W, y*H) so the same graph reads identically at any
            // resolution.
            const w = (layer && layer.resolution) ? layer.resolution.width  : logicalW;
            const h = (layer && layer.resolution) ? layer.resolution.height : logicalH;
            const off = this.acquireEscape(w, h);
            const octx = off.getContext('2d');
            // Draw each particle as a small filled circle with alpha
            // proportional to remaining life. Radius is a scalar of the
            // shorter axis (1% of min(w,h)) so a portrait layer paints the
            // same dot size as a landscape one.
            const baseRadius = Math.max(2, Math.min(w, h) * 0.01);
            const TAU = Math.PI * 2;   // hoisted out of the per-particle loop
            octx.fillStyle = color;
            for (let i = 0; i < state.particles.length; i++) {
                const p = state.particles[i];
                const lifeFrac = 1 - (p.age / p.lifetime);
                octx.globalAlpha = Math.max(0, Math.min(1, lifeFrac));
                octx.beginPath();
                octx.arc(p.x * w, p.y * h, baseRadius, 0, TAU);
                octx.fill();
            }
            octx.globalAlpha = 1;
            return { image: off, width: w, height: h };
        }

        // V7 — wirable Path (see evalImageLoad). ensureVideoElement already handled a
        // changing src correctly (it re-arms the one-shot alpha probe), and the function
        // still ends at the fitLoadedImageToFrame return below, so the contain-fit that a
        // prior sweep added survives a dynamic source. Losing it would report a native-
        // size clip at native size and Display would centre-crop instead of fit.
        async evalVideoLoad(node, socketId) {
            // _evalMediaPathSocket = resolve + the wired-must-be-relative guard; see it and
            // evalImageLoad. A rejected wired path returns '' and bails on the next line.
            const path = await this._evalMediaPathSocket(node);
            if (!path) return null;
            const loop  = String(attr(node, 'Loop',  'true')).toLowerCase() !== 'false';
            const muted = String(attr(node, 'Muted', 'true')).toLowerCase() !== 'false';
            const src   = resolveMediaPath(path);
            const video = ensureVideoElement(node.Id, src, { loop, muted });
            // Drive continuous re-render so the canvas pulls fresh frames from
            // the <video> element each tick.
            requestWidgetAnimator();
            // One-shot alpha probe (per video element instance): the first time
            // a frame is decoded, sample a small region via getImageData and
            // report whether ANY pixel has alpha < 255. Two failure modes this
            // disambiguates for "I encoded my WebM with alpha but it renders
            // opaque":
            //   • "no transparency in any pixel" → either the WebM lacks a
            //     real alpha plane (encoder produced AlphaMode=1 but BlockMore
            //     alpha frames are absent / empty — typically missing
            //     `-auto-alt-ref 0`), or WebView2's hardware decoder dropped
            //     the alpha plane during decode.
            //   • "transparency present" → the source is alpha-capable AND
            //     Chromium decoded it; any remaining black-box look is
            //     downstream (paintBackdrop, OBS source background, etc.).
            // Cheap (≤64×64 sample, runs once per video element) so it stays
            // out of the steady-state render budget. Output goes to the
            // browser console — F12 in WebView2 opens DevTools for inspection.
            if (!video.dataset.alphaProbed
                && video.videoWidth > 0
                && video.videoHeight > 0
                && video.readyState >= 2) {
                video.dataset.alphaProbed = '1';
                try {
                    const probe = document.createElement('canvas');
                    const pw = Math.min(64, video.videoWidth);
                    const ph = Math.min(64, video.videoHeight);
                    probe.width = pw; probe.height = ph;
                    const pctx = probe.getContext('2d');
                    pctx.clearRect(0, 0, pw, ph);
                    pctx.drawImage(video, 0, 0, pw, ph);
                    const px = pctx.getImageData(0, 0, pw, ph).data;
                    let minAlpha = 255;
                    for (let i = 3; i < px.length; i += 4) {
                        if (px[i] < minAlpha) minAlpha = px[i];
                        if (minAlpha === 0) break;
                    }
                    if (minAlpha < 255) {
                        console.info(
                            `[Visualist] Video.Load ${src}: alpha plane decoded ` +
                            `(min α=${minAlpha}/255 in ${pw}×${ph} sample). ` +
                            `Transparency will composite into the layer canvas.`);
                    } else {
                        console.warn(
                            `[Visualist] Video.Load ${src}: decoded frame is fully opaque ` +
                            `(α=255 across ${pw}×${ph} sample). Either the WebM has no real ` +
                            `alpha frames (re-encode with: ffmpeg -i src -c:v libvpx-vp9 ` +
                            `-pix_fmt yuva420p -auto-alt-ref 0 -b:v 2M out.webm) or ` +
                            `WebView2/Chromium dropped alpha during decode.`);
                    }
                } catch (e) {
                    // getImageData throws SecurityError if the canvas is tainted —
                    // shouldn't happen for same-origin /media/ but log so we know.
                    console.warn(
                        `[Visualist] Video.Load ${src}: alpha probe failed (${e.message}). ` +
                        `Likely a tainted-canvas / CORS issue.`);
                }
            }
            // Audit fix — socket-aware output. The Duration scalar socket returns the
            // decoded video length in seconds (0 until metadata loads); previously the
            // Duration output was dead (evalVideoLoad ignored it and returned the image
            // object for every socket, so a Scalar consumer resolved to 0). Mirrors the
            // socketId-aware evalVisualOnTrigger pattern.
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            if (sock && sock.Name === 'Duration') {
                const d = video.duration;
                return Number.isFinite(d) ? d : 0;
            }
            // If metadata isn't ready yet (first frame load), fall back to the
            // widget rect dimensions so Display still gets non-zero w/h.
            const w = video.videoWidth  || 1;
            const h = video.videoHeight || 1;
            // Contain-fit the video to the widget frame, exactly like Image.Load does
            // (fitLoadedImageToFrame). Without this a native-size video (e.g. 1920x1080)
            // was reported at native size and the Display sink drew it 1:1 → centre-
            // cropped to the widget instead of fitting it. No frame bound (legacy /
            // tooling Evaluator) → native size unchanged.
            const fit = fitLoadedImageToFrame(w, h, this.frame);
            return { image: video, width: fit.width, height: fit.height };
        }

        // V7 — wirable Path (see evalImageLoad). This is the loader the per-kind alert
        // sound runs through: one Audio.Load + one Audio.Play, clip chosen by VALUE
        // upstream (String.Select) because branch-gated audio is not expressible — see
        // the audio-sink pass in renderWidgetTrigger for why.
        async evalAudioLoad(node) {
            // _evalMediaPathSocket = resolve + the wired-must-be-relative guard. THE most
            // exposed of the three: this is the loader a chat-driven soundboard wires, so
            // "!sound https://attacker/x.mp3" reaches exactly here. A rejected wired path
            // returns '' and bails on the next line — no audio element, no fetch.
            const path = await this._evalMediaPathSocket(node);
            if (!path) return null;
            // Audio is opaque to the image pipeline — Display would render
            // nothing useful from it. Audio.Play (sink) consumes this shape.
            return { kind: 'audio', src: resolveMediaPath(path) };
        }

        async evalAudioPlay(node) {
            const inLink = this.findLinkTo(node.Id, 'Audio');
            if (!inLink) return null;
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || upstream.kind !== 'audio' || !upstream.src) return null;
            // FALSY-ZERO FIX — `parseFloat(...) || 1` rewrote a legitimate 0 (the mixer's mute
            // position) to full volume, so an alert deliberately muted on the node played at
            // 100% on stream. Only a non-finite read may fall back to the default.
            const rawVolume  = parseFloat(attr(node, 'Volume', '1'));
            const nodeVolume = Math.max(0, Math.min(1, Number.isFinite(rawVolume) ? rawVolume : 1));
            // Track E — scale the node's own Volume by the active trigger's master
            // volume (default 1) and re-clamp to 0..1 so the mixer can attenuate all
            // audio in the trigger without touching per-node attributes.
            const volume = Math.max(0, Math.min(1, nodeVolume * activeTriggerVolume));
            const loop   = String(attr(node, 'Loop', 'false')).toLowerCase() === 'true';
            // Pass THIS WIDGET's activation generation so a one-shot fires once per genuine
            // trigger of this widget — not once per animator/Play/scrub render tick, and not
            // once per activation of some OTHER widget on the layer (see _audioActivationGen).
            ensureAudioElementAndPlay(
                node.Id, upstream.src, { volume, loop, gen: audioActivationGen(this.widgetId) });
            return null;
        }

        async evalImageScale(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) return null;
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const factorLink = this.findLinkTo(node.Id, 'Factor');
            // Sweep 21 — Factor is a canonical animation target; honor keyframes.
            // FALSY-ZERO FIX — same swallow as Image.Transform's scale reads; see the clamp
            // below for why letting 0 through is only half the job.
            const _factorRaw = parseFloat(attrAnimated(node, 'Factor', '1'));
            let factor = Number.isFinite(_factorRaw) ? _factorRaw : 1;
            if (factorLink) {
                const f = await this.evalNodeOutput(factorLink.FromNodeId, factorLink.FromSocketId);
                if (typeof f === 'number') {
                    factor = f;
                } else {
                    // F8 — link exists but upstream returned null / non-numeric. Surface
                    // loudly so authors know which node is dropping the value, and stash
                    // an error glyph flag for the future on-node error overlay to read.
                    console.warn('[Visualist] Image.Scale: upstream Factor returned null/non-numeric; falling back to attribute.', {
                        nodeId:    node.Id,
                        nodeTitle: node.Title,
                        attrFallback: factor,
                        upstreamValue: f,
                    });
                    node._errorGlyph = 'Factor upstream returned null';
                }
            } else {
                // No link — clear any stale error glyph from a prior render.
                if (node._errorGlyph) delete node._errorGlyph;
            }
            // Clamp exactly as NodeEvaluator.EvalImageScale does: a strictly positive floor on
            // the factor, and each side floored at 1px. Necessary because every downstream
            // consumer reads dimensions as `x.width || x.image.width`, so a 0x0 result would be
            // re-inflated to the natural size — the same falsy-zero swallow one node later.
            // Written as `!(factor >= …)` rather than C#'s `factor < …` so a NaN is caught too.
            if (!(factor >= 0.001)) factor = 0.001;
            return {
                image:  upstream.image,
                width:  Math.max(1, Math.round(upstream.width  * factor)),
                height: Math.max(1, Math.round(upstream.height * factor)),
            };
        }

        // F3 — Color.Constant emits an {r, g, b, a} object with components in 0..1.
        // Downstream consumers (notably Text.Render) call colorToCss(...) to render.
        evalColorConstant(node) {
            return parseHexColor(attrAnimatedColor(node, 'Value', '#ffffff'));
        }

        evalScalarConstant(node) {
            // Sweep 21 — constant nodes are the primary animation target. The
            // Animate-parameter gesture (right-click → Animate) hangs a track on
            // their Value socket; attrAnimated samples it at the current timeMs.
            return parseFloat(attrAnimated(node, 'Value', '0')) || 0;
        }

        // #1 — String.Constant runtime eval. Mirrors Color.Constant's quoting
        // contract: the Value attribute is stored quoted (e.g. "\"hello\"") to
        // match the C# NodeTemplates default (["Value"] = "\"\""), so we strip
        // the surrounding quotes before returning the literal text. The C#
        // NodeEvaluator string case does the same via Unquote(attr "Value").
        evalStringConstant(node) {
            return stripQuotes(attr(node, 'Value', ''));
        }

        evalVector2Constant(node) {
            return {
                x: parseFloat(attrAnimated(node, 'X', '0')) || 0,
                y: parseFloat(attrAnimated(node, 'Y', '0')) || 0,
            };
        }

        // M56 / F12 — Vector3 / Vector4 constant producers. Same attribute-driven
        // shape as Vector2.Constant; absent components default to 0 so a partially-
        // edited node still resolves to a well-formed vector.
        evalVector3Constant(node) {
            return {
                x: parseFloat(attrAnimated(node, 'X', '0')) || 0,
                y: parseFloat(attrAnimated(node, 'Y', '0')) || 0,
                z: parseFloat(attrAnimated(node, 'Z', '0')) || 0,
            };
        }
        evalVector4Constant(node) {
            return {
                x: parseFloat(attrAnimated(node, 'X', '0')) || 0,
                y: parseFloat(attrAnimated(node, 'Y', '0')) || 0,
                z: parseFloat(attrAnimated(node, 'Z', '0')) || 0,
                w: parseFloat(attrAnimated(node, 'W', '0')) || 0,
            };
        }
        // F12 — Vector.Rect4 is a friendly alias for Image.Crop's Rect input. Stored
        // as X/Y/W/H attribute keys; emitted as the canonical {x,y,z,w} vector shape so
        // downstream consumers (resolveVector4Socket, Image.Crop) treat it uniformly.
        evalVectorRect4(node) {
            return {
                x: parseFloat(attr(node, 'X', '0')) || 0,
                y: parseFloat(attr(node, 'Y', '0')) || 0,
                z: parseFloat(attr(node, 'W', '0')) || 0,
                w: parseFloat(attr(node, 'H', '0')) || 0,
            };
        }

        /// Resolves a Vector4 socket (used by Image.Crop's Rect socket). Walks the
        /// upstream link if one exists; returns null when the socket isn't wired
        /// (caller falls back to attribute parsing).
        async resolveVector4Socket(node, socketName) {
            const link = this.findLinkTo(node.Id, socketName);
            if (!link) return null;
            const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
            if (v && typeof v === 'object' && 'x' in v && 'y' in v && 'z' in v && 'w' in v) return v;
            return null;
        }

        /// Caption.LiveCaption emits two outputs — the untranslated stream and the
        /// post-translator one — reading `caption.original` / `caption.translated` off the
        /// channel. Compositor.js can't see which output the caller asked for from socketId
        /// alone, so we look up the node's socket by id and dispatch on its name.
        ///
        /// L49 — names sourced from CAPTION_SOCKETS so a C# rename can be tracked in one place;
        /// an unrecognized socket name is warned about (once per name) instead of silently
        /// falling through. The template actually names its first output "Text", which is why
        /// CAPTION_ORIGINAL_SOCKETS exists — see that constant.
        ///
        /// Translated falls back to the original when only the original is live, which is the
        /// correct degradation for a translator that has not answered yet (and for a
        /// TargetLang the streamer left empty, where Hub publishes the two keys identically).
        evalCaptionLive(node, socketId) {
            const live = liveRenderableValue(LIVE_KEY_CAPTION_ORIGINAL);
            // Design-time only: an empty caption widget in the editor is indistinguishable from
            // a broken one, so the mock earns its place there. In OBS a caption nobody is
            // speaking renders nothing.
            const original = live === undefined ? liveMock(node) : liveTextOf(live);

            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            if (!sock) return original;
            if (sock.Name === CAPTION_SOCKETS.TRANSLATED) {
                return liveTextOf(liveRenderableValue(LIVE_KEY_CAPTION_TRANSLATED)) || original;
            }
            if (CAPTION_ORIGINAL_SOCKETS.has(sock.Name)) {
                return original;
            }
            if (!_warnedUnknownCaptionSockets.has(sock.Name)) {
                _warnedUnknownCaptionSockets.add(sock.Name);
                console.warn(
                    `[Visualist] Caption.LiveCaption: unrecognized socket name "${sock.Name}" — ` +
                    `expected one of ${JSON.stringify(Object.values(CAPTION_SOCKETS))} ` +
                    `or ${JSON.stringify(Array.from(CAPTION_ORIGINAL_SOCKETS))}. ` +
                    'Falling back to the original stream. Check NodeTemplates.cs vs CAPTION_SOCKETS.');
            }
            return original;
        }

        /// Timer.Remaining / Countdown.Remaining / Stopwatch.Elapsed — the live Hub-timer
        /// readout, sourced from the channel's `timer.<root>.*` family. Dispatch is on the
        /// requested output socket's name; the root comes from liveTimerRoot, THE same helper
        /// liveKeysForNode subscribed with, so the key read and the key asked for cannot drift.
        ///
        /// Each socket reads exactly ONE field:
        ///   State    — the timer's RUN state: Running / Paused / Stopped / Ended, read from the
        ///              VALUE of `<root>state`. This is the meaning State has had since the node
        ///              shipped and it is PRESERVED — widgets in the wild branch on 'Paused' and
        ///              on 'Ended', and quietly redefining the pin to a liveness word broke every
        ///              one of them while the graph still looked correct.
        ///              ⚠ The asymmetry a reader will trip over: on the other channel readers
        ///              that HAVE a State socket (Counter.Value, Loyalty.Leaderboard,
        ///              Loyalty.Balance, Var.Live) "State" means LIVENESS, because that is what
        ///              State already meant on those nodes before the channel. "State" is
        ///              therefore per-node shorthand for "the most useful status this node has".
        ///              The timer trio is the one family with TWO statuses worth reporting, so it
        ///              got a second pin appended rather than a redefinition of the first.
        ///              Caption.LiveCaption is NOT in that list: it reads channel keys but
        ///              declares no State socket at all — only Text and Translated (see
        ///              evalCaptionLive, which has no State arm).
        ///              ⚠ And the liveness VOCABULARY is not uniform either, which matters when
        ///              an author writes a Result.If branch:
        ///                • timer.* keys — Active / Stale / Missing. The Timer tool is the ONLY
        ///                  publisher that declares an ExpectedInterval, and Hub's ComputeState
        ///                  can only return Stale for a key that declared one. So a Stale branch
        ///                  is meaningful HERE, on the Live pin, and on a Var.Live bound to a
        ///                  timer.* key.
        ///                • counter.* / loyalty.* / caption.* and any key a script publishes with
        ///                  overlay.publish — Active / Missing only. They are event-driven and
        ///                  promise no cadence, so they never decay. A widget branching
        ///                  Equals="Stale" on one of them can never fire.
        ///                • loyalty.leaderboard is the one vocabulary exception: its State pin
        ///                  reports Empty rather than Missing for a board with no rows (see
        ///                  evalLoyaltyLeaderboard for that rule and why it is load-bearing).
        ///   Live     — the timer family's LIVENESS: Active / Stale / Missing. Judged on
        ///              `<root>state`, which exists if and only if a timer resolved under this
        ///              root — deliberately NOT `<root>slug`, which Hub publishes even when
        ///              there is no default timer at all and would report Active for nothing.
        ///              Note it can read Stale while State reads 'Running': the run state is the
        ///              last one published, and its age is exactly what this pin is for.
        ///   Progress — 0..1, clamped Hub-side.
        ///   Seconds  — `display_seconds`, i.e. the SAME mode-aware value Text formats, so a
        ///              stopwatch's Seconds counts up exactly as its Text does. Not
        ///              remaining_seconds, which would count the wrong way for a stopwatch.
        ///   Paused   — the paused flag as text ('true' / 'false').
        ///   Mode     — the TimerMode name (Countdown / Stopwatch / …).
        ///   Text     — short | long | clock, per the Format attribute.
        ///
        /// Only Text carries a design-time mock: PreviewText is a formatted duration
        /// ("01:23:45"), which is meaningless as a stand-in for Progress, Paused or Mode.
        evalTimerRemaining(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const root = liveTimerRoot(node);

            if (socketName === 'State') {
                // The RUN state, from the key's VALUE — never the liveness verdict. A stale but
                // known timer keeps reporting the run state it last published (that is what the
                // Live pin is for), so this pin says 'Running' / 'Paused' / 'Stopped' / 'Ended'
                // exactly as it did before the channel existed.
                //
                // No entry at all means no timer resolved under this root, so there is no run
                // state to name: fall through to the liveness vocabulary ('Missing') rather than
                // hand a widget the empty string liveTextOf(undefined) would give it. That keeps
                // an unresolvable TimerName visibly wrong instead of invisibly blank.
                const stateKey = root + 'state';
                if (!liveEntry(stateKey)) return liveStateOf(stateKey);
                return liveTextOf(liveRenderableValue(stateKey));
            }
            // The appended liveness pin. Same key State judges presence on, read for its
            // provenance instead of its value.
            if (socketName === 'Live')     return liveStateOf(root + 'state');
            if (socketName === 'Progress') return liveNumberOf(liveRenderableValue(root + 'progress'));
            if (socketName === 'Seconds')  return liveNumberOf(liveRenderableValue(root + 'display_seconds'));
            if (socketName === 'Paused')   return liveTextOf(liveRenderableValue(root + 'paused'));
            if (socketName === 'Mode')     return liveTextOf(liveRenderableValue(root + 'mode'));

            const fmt = stripQuotes(attr(node, 'Format', 'short'));
            const field = fmt === 'long' ? 'long' : (fmt === 'clock' ? 'clock' : 'short');
            const value = liveRenderableValue(root + field);
            // The fake-fallback kill, in one line: no live timer means the mock at design time
            // and NOTHING in production. The pre-channel reader painted PreviewText in OBS, so
            // a stopped-or-unnamed timer showed a convincing frozen "01:23:45" on stream.
            if (value === undefined) return liveMock(node);
            return liveTextOf(value);
        }

        /// Clock.Now — live digital wall-clock. Browser-autonomous: reads the OBS
        /// machine's own clock (Date.now) each 1 Hz clock heartbeat, shifts it by
        /// UtcOffset hours, and formats per the Format attribute. Needs no Hub state.
        /// Tokens: HH (24h) · hh (12h) · mm · ss · A (AM/PM). We read the SHIFTED
        /// instant's UTC fields so the OBS machine's own timezone never leaks in —
        /// offset 0 = true UTC, offset 2 = UTC+2, etc.
        evalClockNow(node) {
            const offsetHours = parseFloat(stripQuotes(attr(node, 'UtcOffset', '0'))) || 0;
            const fmt = stripQuotes(attr(node, 'Format', 'HH:mm:ss')) || 'HH:mm:ss';
            const shifted = new Date(Date.now() + offsetHours * 3600000);
            const H24 = shifted.getUTCHours();
            const mm  = shifted.getUTCMinutes();
            const ss  = shifted.getUTCSeconds();
            const H12 = (H24 % 12) || 12;
            const p2  = n => (n < 10 ? '0' + n : '' + n);
            // Two-char tokens first, then the AM/PM token last (the digit
            // substitutions never re-introduce a token character).
            return fmt
                .replace(/HH/g, p2(H24))
                .replace(/hh/g, p2(H12))
                .replace(/mm/g, p2(mm))
                .replace(/ss/g, p2(ss))
                .replace(/A/g,  H24 >= 12 ? 'PM' : 'AM');
        }

        /// Loyalty.Leaderboard — live viewer-points leaderboard readout, sourced from the
        /// channel's `loyalty.leaderboard` (a real JSON array of { rank, name, balance }) plus
        /// `loyalty.currency` for the {currency} token. Dispatch is on the output socket's name.
        ///
        ///   State                 — Active / Stale / EMPTY. The ONE reader whose State vocabulary
        ///                           is not the plain Active/Stale/Missing triple, and the
        ///                           deliberate asymmetry is back-compat: before the channel this
        ///                           pin answered 'Empty' for a board with no rows, INCLUDING a
        ///                           board that had never been published (the pre-channel reader
        ///                           held the board in memory, so "never arrived" and "arrived
        ///                           empty" were the same zero-length array to it). Widgets branch
        ///                           `Equals="Empty"` to draw a "No scores yet" card, so 'Empty'
        ///                           has to cover BOTH no-data cases or those cards stop drawing.
        ///                           THE RULE, and it must survive every rework of this reader:
        ///                             • Stale                     → 'Stale'  (kept distinct — the
        ///                               rows are still painted, just a beat old, and a widget
        ///                               hiding on Empty must not hide on Stale)
        ///                             • no rows, for ANY reason   → 'Empty'  (never published,
        ///                               or published as an empty array — indistinguishable to a
        ///                               widget, and both mean "nothing to show")
        ///                             • rows present              → 'Active'
        ///                           'Missing' is therefore never reported HERE. That is not an
        ///                           oversight: Loyalty.Balance next door does report Missing, for
        ///                           the different question "is this VIEWER on the board".
        ///   Rank / Name / Balance  — ONE row, addressed by the Index attribute. Index is
        ///                           1-BASED and reads as a rank: Index 1 is first place, which
        ///                           is what makes the Rank this node reports equal the Index
        ///                           the author typed. Out of range yields '' / 0 — never a
        ///                           wrapped or clamped row, because silently showing rank 1
        ///                           where the author asked for rank 12 is worse than a blank.
        ///   Text (default)        — the top `Size` rows, newline-joined through the Format
        ///                           template.
        evalLoyaltyLeaderboard(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';

            const board = liveLeaderboardRows();

            if (socketName === 'State') {
                // See the doc comment above for the rule and why it is load-bearing. Only
                // Stale short-circuits: it is the one verdict a widget must be able to tell
                // apart from 'Empty', because a stale board still HAS rows and still paints
                // them (liveRenderableValue), so a widget that hides itself on Empty must not
                // hide on Stale. Everything else falls through to the row count, which folds
                // never-published (Missing → zero rows) and published-but-empty into the one
                // 'Empty' answer the pre-channel pin gave for both.
                if (liveStateOf(LIVE_KEY_LOYALTY_BOARD) === 'Stale') return 'Stale';
                return board.length ? 'Active' : 'Empty';
            }

            if (socketName === 'Rank' || socketName === 'Name' || socketName === 'Balance') {
                const idx = liveLeaderboardIndex(node);
                const row = (idx >= 0 && idx < board.length) ? (board[idx] || null) : null;
                if (socketName === 'Name')    return (row && row.name != null) ? String(row.name) : '';
                if (socketName === 'Balance') return row ? liveNumberOf(row.balance) : 0;
                // Rank: prefer the row's own rank (Hub owns the ordering), fall back to the
                // 1-based position so a payload without ranks still reads sensibly.
                return row ? (row.rank != null ? liveNumberOf(row.rank) : idx + 1) : 0;
            }

            // No rows to paint → the mock at design time, NOTHING in production. This is the
            // single most user-visible line in the rework: the shipped PreviewText default is
            // "1. viewer_one — 12,400 / 2. viewer_two — 9,830 / …", and the pre-channel reader
            // painted it in OBS whenever the board was empty — which, with "shut down source
            // when not visible", was every scene return until the first LOYALTY_UPDATE landed.
            // A STALE board does NOT land here: it keeps its last rows and keeps rendering them,
            // with the staleness reported on the State pin (see liveRenderableValue).
            if (!board.length) return liveMock(node);

            let size = parseInt(stripQuotes(attr(node, 'Size', '10')), 10);
            if (!Number.isFinite(size) || size <= 0) size = board.length;

            const fmt = stripQuotes(attr(node, 'Format', '{rank}. {name} — {balance}'));
            const currency = liveTextOf(liveRenderableValue(LIVE_KEY_LOYALTY_CURRENCY));
            const count = Math.min(size, board.length);
            const lines = [];
            for (let i = 0; i < count; i++) {
                const e = board[i] || {};
                const rank = (e.rank != null) ? e.rank : (i + 1);
                lines.push(this.formatLoyaltyLine(fmt, rank, e.name, e.balance, currency));
            }
            return lines.join('\n');
        }

        /// Loyalty.Balance — single-viewer points readout. Derived from the SAME
        /// `loyalty.leaderboard` array (per-user balance keys are deliberately not published —
        /// that family is unbounded), matching the User attribute case-insensitively.
        ///
        ///   State   — liveness first: a Missing or Stale board is exactly that, whoever the
        ///             user is. With a live board, "this viewer is not on it" is honestly
        ///             Missing — which both preserves the per-user signal the pre-channel
        ///             reader gave and stays inside the Active/Stale/Missing vocabulary.
        ///             Stale here would mean "the balance below is a few seconds old", not
        ///             "blank": the row still resolves off the last board (see
        ///             liveRenderableValue). In PRACTICE it never fires — `loyalty.leaderboard`
        ///             declares no ExpectedInterval (balances are event-driven), so Hub's
        ///             ComputeState can only ever answer Active or Missing for it. The arm stays
        ///             because the verdict is the store's to define, not this reader's; do not
        ///             advertise Stale to authors here (see evalTimerRemaining's vocabulary note).
        ///   Balance — the viewer's balance, 0 when unresolved.
        ///   Text    — the Format line for the matched row; the mock at design time and
        ///             NOTHING in production when unresolved. The old fallback rendered the
        ///             template with a literal zero, so an unknown or misspelled viewer showed
        ///             a confident "someviewer: 0" on stream.
        ///
        /// A {currency} token is substituted EMPTY here, on purpose: per the reader matrix this
        /// node subscribes only `loyalty.leaderboard`, so printing a currency label would mean
        /// printing a value we never asked Hub for and whose freshness we cannot vouch for.
        evalLoyaltyBalance(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const user = stripQuotes(attr(node, 'User', ''));
            const row  = liveLeaderboardRow(user);

            if (socketName === 'State') {
                const boardState = liveStateOf(LIVE_KEY_LOYALTY_BOARD);
                if (boardState !== 'Active') return boardState;
                return row ? 'Active' : 'Missing';
            }
            if (socketName === 'Balance') return row ? liveNumberOf(row.balance) : 0;

            if (!row) return liveMock(node);
            const fmt = stripQuotes(attr(node, 'Format', '{name}: {balance}'));
            return this.formatLoyaltyLine(fmt, row.rank, row.name, row.balance, '');
        }

        /// Substitutes the {rank}/{name}/{balance}/{currency} tokens in a Loyalty.* Format line.
        /// Balance is thousands-grouped (12400 → "12,400"); a non-numeric balance is emitted
        /// verbatim. A blank rank leaves {rank} empty. `currency` defaults to '' so a caller
        /// that has no subscribed currency key substitutes the token away rather than leaking
        /// the literal "{currency}" onto the canvas.
        formatLoyaltyLine(fmt, rank, name, balance, currency = '') {
            const n = Number(balance);
            const balStr = Number.isFinite(n)
                ? n.toLocaleString('en-US')
                : String(balance == null ? '' : balance);
            return String(fmt)
                .replace(/\{rank\}/g, (rank == null || rank === '') ? '' : String(rank))
                .replace(/\{name\}/g, name == null ? '' : String(name))
                .replace(/\{balance\}/g, balStr)
                .replace(/\{currency\}/g, currency == null ? '' : String(currency));
        }

        /// Counter.Value — live named-counter readout, sourced from the channel's
        /// `counter.<name>.count`. The key IS the counter, so key-liveness and
        /// counter-existence are ALMOST the same question — with the one exception the State
        /// entry records below, State needs no special case:
        ///
        ///   State — Active or Missing for that one key. Missing means "nothing has ever been
        ///           published under it": a counter that has never moved, a misspelled name, or
        ///           a node that names none at all.
        ///           ⚠ Missing is NOT the same as "the counter was deleted". OverlayLiveStore
        ///           has no per-key remove and no TTL, so once a key has been published it can
        ///           never go back to Missing. CountersService retracts a deleted ad-hoc
        ///           counter by publishing JSON null, which blanks Text and zeroes Value but
        ///           still reads Active here — see its RetractCount, which records the same
        ///           limit from the publisher's side. So `Result.If Equals="Missing" → hide`
        ///           catches the never-published case only; branch on the blank Text to catch
        ///           a delete as well.
        ///
        ///           Stale is NOT reachable here: `counter.*` is
        ///           event-driven and declares no ExpectedInterval, so Hub's ComputeState never
        ///           returns it for this family — do not advertise a Stale branch to authors on
        ///           this node (see evalTimerRemaining's vocabulary note). Were it ever to
        ///           arrive, it would not blank the readout: Value and Text keep serving the last
        ///           published count (see liveRenderableValue) and this pin is the only place the
        ///           age would show up.
        ///   Value — the count as a number, 0 when unresolved.
        ///   Text  — the Format line; the mock at design time, NOTHING in production. The old
        ///           fallback rendered the template with a literal zero, so an unnamed or
        ///           misspelled counter showed a confident "0" on stream — and a "deaths: 0"
        ///           overlay is indistinguishable from a real counter that hasn't moved.
        evalCounterValue(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const key = liveCounterKey(node);

            if (socketName === 'State') return liveStateOf(key);

            const value = liveRenderableValue(key);
            if (socketName === 'Value') return liveNumberOf(value);

            if (value === undefined) return liveMock(node);
            const name = stripQuotes(attr(node, 'Name', ''));
            const fmt  = stripQuotes(attr(node, 'Format', '{count}'));
            return this.formatCounterLine(fmt, name, value);
        }

        /// Var.Live — the author-facing binding node: ONE Overlay Live Channel key, read by its
        /// literal name, whoever published it. `timer.main.progress` and a hand-published
        /// `boss_hp` bind identically; that symmetry ("no special treatment") is the point of
        /// the channel.
        ///
        /// Value typing lives HERE rather than at the publisher because this is where the author
        /// expressed intent by choosing a pin: overlay.publish stores every author value as a
        /// JSON string and deliberately refuses to sniff whether the text looks numeric, so
        /// "007" survives as "007". Tool keys keep their real JSON types, which makes Number
        /// exact for them and best-effort for author strings.
        ///
        ///   Text   — the value as text (JSON string → its content; number/bool → its literal;
        ///            array/object → compact JSON), or the design-time mock when there is nothing
        ///            paintable.
        ///   Number — invariant parse; 0 on failure, never NaN.
        ///   State  — Active / Stale / Missing. Liveness, as it always was on this node — unlike
        ///            the timer trio, where State is the RUN state and a separate Live pin carries
        ///            the liveness. See evalTimerRemaining for why that asymmetry exists.
        ///            This is the ONE reader where all three words are reachable, and only
        ///            because the bound key decides: Stale requires a key whose publisher declared
        ///            an ExpectedInterval, and `timer.*` is the only family that does. Bind a
        ///            `counter.*` / `loyalty.*` / `caption.*` key, or one your own script published
        ///            with overlay.publish, and this pin can only ever answer Active or Missing.
        ///
        /// PreviewText is read for the Text pin, and ONLY on a design-time surface (liveMock
        /// enforces that, not this reader). The template ships the attribute and all four lang
        /// bubbles advertise it as an author-editable placeholder, so a node that ignored it was
        /// inert on exactly the two surfaces the author looks at while building — the ?widget=
        /// preview and the capture thumbnail — while promising otherwise in the UI. In production
        /// an unbound or never-published key still renders NOTHING; that gate is the reason the
        /// mock is allowed to exist at all.
        ///
        /// Number and State stay honest on EVERY surface (0 / the real verdict). A mocked number
        /// would silently poison a Math chain the author is trying to debug, and a mocked verdict
        /// would hide the very condition the verdict exists to report.
        evalVarLive(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const key = liveVarKey(node);

            if (socketName === 'State')  return liveStateOf(key);
            const value = liveRenderableValue(key);
            if (socketName === 'Number') return liveNumberOf(value);
            // Text — symmetric with the five other channel readers: nothing paintable means the
            // mock at design time and '' in production. liveMock owns the surface gate, so the
            // only decision here is "is there a value", and 'Stale' is NOT that decision (a stale
            // key still has its last value and paints it).
            if (value === undefined) return liveMock(node);
            return liveTextOf(value);
        }

        /// Goal.Progress — the goal.<kind>.* family reader (V10). One root, four published
        /// fields, six pins: Text / State / Progress / Current / Target / Label.
        ///
        /// Why a node rather than four Var.Live bindings: Var.Live can already bind any one of
        /// the four keys, so this buys nothing an author could not wire by hand — what it
        /// removes is the four chances to mistype a dotted key. The subscription is derived from
        /// attribute TEXT at graph-scan time, so a typo'd key is a permanently blank pin with a
        /// valid graph, a running publisher and no error anywhere. One Kind box, one prefix
        /// subscription, four pins that cannot drift.
        ///
        ///   State    — Active / Stale / Missing for the whole root (liveGoalState), so a
        ///              partial publisher still reads Active.
        ///   Progress — 0..1, clamped. A published progress wins; current/target is the
        ///              documented fallback so a script that publishes only those two still
        ///              gets a working bar.
        ///   Current  — the exact published number, 0 when unpublished.
        ///   Target   — likewise.
        ///   Label    — the publisher's display label, '' when unpublished. It must NOT fall
        ///              back to PreviewText: that attribute holds a whole formatted LINE, so a
        ///              pin carrying one label would hand its consumer the entire mock.
        ///   Text     — Format rendered; the design-time mock when nothing at all is published.
        ///
        /// Only Text carries a mock, and only through liveMock — so in production an
        /// unpublished goal renders NOTHING. That gate is the whole point of the rework: the
        /// pre-channel readers painted their PreviewText in OBS, which is how fake data reached
        /// live streams on every scene return.
        evalGoalProgress(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const root = liveGoalRoot(node);

            if (socketName === 'State') return liveGoalState(root);

            // A node with no Kind can never resolve, and it must not fall through to the
            // bare-field keys liveGoalRoot refuses to build. Numeric pins read 0, Label reads
            // '', and Text is the only pin allowed the design-time mock.
            if (!root) {
                if (socketName === 'Progress' || socketName === 'Current' || socketName === 'Target') return 0;
                if (socketName === 'Label') return '';
                return liveMock(node);
            }

            const currentV   = liveRenderableValue(root + 'current');
            const targetV    = liveRenderableValue(root + 'target');
            const publishedP = liveRenderableValue(root + 'progress');
            const labelV     = liveRenderableValue(root + 'label');

            if (socketName === 'Current')  return liveNumberOf(currentV);
            if (socketName === 'Target')   return liveNumberOf(targetV);
            if (socketName === 'Progress') return goalProgressOf(publishedP, currentV, targetV);
            if (socketName === 'Label')    return liveTextOf(labelV);

            // Text. "Nothing paintable" is the whole ROOT being unpublished — one filled field
            // is a working goal and prints, with the unfilled tokens substituted empty (the same
            // rule Loyalty's blank {rank} follows). A STALE root does NOT land here: it keeps
            // its last values and keeps printing them, with the age reported on State.
            if (currentV === undefined && targetV === undefined
                && publishedP === undefined && labelV === undefined) {
                return liveMock(node);
            }
            return this.formatGoalLine(
                stripQuotes(attr(node, 'Format', '{current} / {target}')),
                currentV, targetV,
                goalProgressOf(publishedP, currentV, targetV),
                liveTextOf(labelV),
                root.slice(GOAL_KEY_PREFIX.length, -1));
        }

        /// Substitutes the goal tokens in a Goal.Progress Format line:
        ///   {current} {target}  — thousands-grouped, matching the Counter and Loyalty
        ///                         formatters. An UNPUBLISHED side substitutes EMPTY rather
        ///                         than 0: "120 / " is honest about a missing target, while
        ///                         "120 / 0" invents one.
        ///   {progress}          — the 0..1 fraction, trimmed to at most three decimals so a
        ///                         float artefact ("0.6180000000000001") never reaches a canvas.
        ///   {percent}           — the fraction as a whole number, WITHOUT a sign, so the author
        ///                         writes "{percent}%" and controls their own spacing.
        ///   {label} {kind}      — the publisher's label and the resolved kind slug.
        formatGoalLine(fmt, currentV, targetV, progress, label, kind) {
            const num = v => {
                if (v === undefined) return '';
                const n = Number(v);
                return Number.isFinite(n) ? n.toLocaleString('en-US') : liveTextOf(v);
            };
            const frac = Number.isFinite(progress) ? progress : 0;
            return String(fmt)
                .replace(/\{current\}/g,  num(currentV))
                .replace(/\{target\}/g,   num(targetV))
                .replace(/\{progress\}/g, String(Number(frac.toFixed(3))))
                .replace(/\{percent\}/g,  String(Math.round(frac * 100)))
                .replace(/\{label\}/g,    label == null ? '' : String(label))
                .replace(/\{kind\}/g,     kind == null ? '' : String(kind));
        }

        /// List.Live — the channel ARRAY reader (V10). Binds ONE literal key whose value is a
        /// JSON array and exposes the rows four ways: all of them joined (Text), one of them
        /// formatted (Row), one FIELD of one of them raw (Value), and that field as a number.
        ///
        /// Var.Live handles any single value; on an array its Text pin yields compact JSON,
        /// which is unpaintable. The only list reader in the catalog was Loyalty.Leaderboard and
        /// it is hardwired to one key with three loyalty tokens. Eight members of the
        /// channel-fed widget family are list-shaped (event list, tip ticker, top-donator board,
        /// viewer queue, emote wall, end credits, sponsor rotator, poll rows), so this is ONE
        /// node standing in for eight per-tool readers.
        ///
        ///   State  — Active / Stale / EMPTY. 'Empty' rather than 'Missing' for a row-less list,
        ///            matching Loyalty.Leaderboard: widgets branch on that word to draw a
        ///            "nothing yet" card, and a never-published list and a published-empty one
        ///            are the same thing to a widget. Stale stays distinct — a stale list keeps
        ///            painting its last rows, so a widget hiding on Empty must not hide on Stale.
        ///   Count  — how many rows the array holds.
        ///   Row    — the addressed row, Format-templated.
        ///   Value  — row[Field], raw and unformatted. This is the pin that feeds a wirable
        ///            Image.Load Path for an emote wall or a sponsor logo.
        ///   Number — row[Field] as a number; 0 on failure, never NaN.
        ///   Text   — the top Size rows, Format-templated and newline-joined. Text.Render draws
        ///            those rows AS rows (its multi-line pass landed with this node).
        ///
        /// Index is a wirable Scalar with the attribute as its fallback, which is what turns
        /// this node into a rotator: a Time.Sawtooth scaled by Count and floored drives a tip
        /// ticker or a sponsor carousel with no further nodes. It is 1-BASED, matching
        /// Loyalty.Leaderboard's Index — the rows a board prints are 1-based, so any other
        /// choice would put this node's own two numbers in contradiction. Out of range yields
        /// '' / 0 and never a wrapped row: showing row 1 where the author asked for row 12 is a
        /// wrong answer dressed as a right one.
        ///
        /// Only Text carries the design-time mock, through liveMock, so an unpublished list
        /// renders NOTHING in production. The per-row pins stay empty even at design time — see
        /// the note at the Row arm for why slicing the mock would be dishonest rather than kind.
        async evalListLive(node, socketId) {
            const sock = (node.Sockets || []).find(s => s.Id === socketId);
            const socketName = sock ? sock.Name : '';
            const key  = liveListKey(node);
            // widgetId only decorates the malformed-string diagnostic Hub logs — see liveListRows.
            const rows = liveListRows(key, this.widgetId);

            if (socketName === 'State') {
                // Only Stale short-circuits: a stale list still HAS rows and still paints them,
                // so a widget that hides itself on Empty must not hide on Stale. Everything else
                // folds never-published and published-but-empty into the one 'Empty' answer.
                if (key && liveStateOf(key) === 'Stale') return 'Stale';
                return rows.length ? 'Active' : 'Empty';
            }
            if (socketName === 'Count') return rows.length;

            const fmt   = stripQuotes(attr(node, 'Format', '{index}. {name}'));
            const field = stripQuotes(attr(node, 'Field', 'name'));

            if (socketName === 'Row' || socketName === 'Value' || socketName === 'Number') {
                const idx = await this._listLiveIndex(node);
                const row = (idx >= 0 && idx < rows.length) ? rows[idx] : null;
                if (socketName === 'Number') return liveNumberOf(liveListField(row, field));
                if (socketName === 'Value')  return liveTextOf(liveListField(row, field));
                // Row. Empty when unresolved, on EVERY surface including design time — and not
                // because a mock would be hard to produce. PreviewText holds already-FORMATTED
                // lines, so slicing line N out of it would hand this pin the output of a
                // template it never applied, while the Value and Number pins beside it (which
                // need raw field data the mock does not contain) stayed empty. A Row that mocks
                // while its own siblings cannot is worse than three honest blanks. Same call
                // Loyalty.Leaderboard's per-row pins make.
                if (row === null || row === undefined) return '';
                return this.formatListRow(fmt, row, idx + 1);
            }

            // Text — the joined block. No rows means the mock at design time and NOTHING in
            // production. A STALE list does not land here (it keeps its rows).
            if (!rows.length) return liveMock(node);

            // Size 0 / non-numeric means "all of them", the same forgiving default
            // Loyalty.Leaderboard's Size takes.
            let size = parseInt(stripQuotes(attr(node, 'Size', '10')), 10);
            if (!Number.isFinite(size) || size <= 0) size = rows.length;
            const count = Math.min(size, rows.length);
            const lines = [];
            for (let i = 0; i < count; i++) lines.push(this.formatListRow(fmt, rows[i], i + 1));
            return lines.join('\n');
        }

        /// The 0-BASED row a List.Live's Row / Value / Number pins address, from its 1-based
        /// Index. Wired Scalar wins over the attribute (_evalAnimScalarSocket), which is what
        /// lets a Time.Sawtooth rotate the addressed row.
        ///
        /// Floored, because a rotator's driver is a continuous ramp rather than an integer. A
        /// missing, non-numeric or sub-1 Index means the first row — the same forgiving default
        /// liveLeaderboardIndex takes. Out-of-range is NOT clamped here; the caller renders
        /// '' / 0 (see evalListLive).
        async _listLiveIndex(node) {
            const raw = await this._evalAnimScalarSocket(node, 'Index', 1);
            const n = Math.floor(Number(raw));
            return (Number.isFinite(n) && n >= 1) ? n - 1 : 0;
        }

        /// Renders ONE list row through a Format template.
        ///
        /// FORMAT TOKENS ARE FIELD NAMES, which is the design decision that lets one node serve
        /// eight list-shaped widgets: {index} is the 1-based row position, {value} is the row
        /// itself when the array holds bare strings or numbers, and every other {token} is
        /// looked up as a FIELD of the row object, case-insensitively. So a publisher chooses
        /// its own row shape — { name, amount } or { label, votes } or { user, months } — and no
        /// row schema is baked into the palette.
        ///
        /// ★ ONLY AN ACTUAL JSON NUMBER IS THOUSANDS-GROUPED. A published STRING is emitted
        /// verbatim, whatever it looks like — and that split is a correctness requirement, not a
        /// style choice, because this formatter's tokens are arbitrary publisher FIELD NAMES
        /// rather than a fixed vocabulary. Grouping anything that merely looked numeric corrupted
        /// real data with no opt-out: a tip ticker publishing { name: "12345678", amount: "5.00" }
        /// rendered "12,345,678 tipped 5" — a comma injected into an all-digit Twitch login (which
        /// is legal) and the cents silently dropped. "2026" became "2,026" and "007" became "7".
        ///
        /// This does NOT match the sibling formatters, and an earlier version of this comment
        /// claimed it did. formatLoyaltyLine coerces ONE nominated token ({balance}) and emits
        /// {name} / {rank} verbatim; formatCounterLine coerces only {count}. Both know which token
        /// is a number because their row shape is fixed. Here nothing is fixed, so the VALUE's own
        /// JSON type is the only honest signal — and overlay.publish preserves it (tool keys keep
        /// real numbers, script keys are strings by design, see liveNumberOf).
        ///
        /// {index} is unaffected: it is this formatter's own row counter, never publisher data.
        ///
        /// An unresolvable token renders EMPTY rather than leaking its own braces onto the
        /// canvas: a literal "{amount}" on a live overlay reads as a broken widget, whereas a
        /// gap reads as missing data, which is what it is.
        formatListRow(fmt, row, index) {
            return String(fmt).replace(/\{([A-Za-z0-9_]+)\}/g, (_, token) => {
                if (token.toLowerCase() === 'index') return String(index);
                // Every other token goes through the ONE field resolver, including {value}: on
                // an OBJECT row it looks up a field literally named "value" (so a publisher can
                // have one), and on a BARE row liveListField returns the row itself whatever the
                // token was — which is what makes the default "{index}. {name}" template work
                // unchanged on an array of plain strings.
                const raw = liveListField(row, token);
                if (raw === undefined || raw === null) return '';
                if (typeof raw === 'number') {
                    return Number.isFinite(raw) ? raw.toLocaleString('en-US') : '';
                }
                return liveTextOf(raw);
            });
        }

        /// Substitutes the {name}/{count} tokens in a Counter.Value Format line.
        /// Count is thousands-grouped (12400 → "12,400") to match the Loyalty style;
        /// a non-numeric count is emitted verbatim.
        formatCounterLine(fmt, name, count) {
            const n = Number(count);
            const countStr = Number.isFinite(n)
                ? n.toLocaleString('en-US')
                : String(count == null ? '' : count);
            return String(fmt)
                .replace(/\{name\}/g, name == null ? '' : String(name))
                .replace(/\{count\}/g, countStr);
        }

        async evalTextTranslate(node) {
            const textLink = this.findLinkTo(node.Id, 'Text');
            const langLink = this.findLinkTo(node.Id, 'TargetLang');
            const text = textLink
                ? (await this.evalNodeOutput(textLink.FromNodeId, textLink.FromSocketId)) || ''
                : stripQuotes(attr(node, 'Text', ''));
            const lang = langLink
                ? (await this.evalNodeOutput(langLink.FromNodeId, langLink.FromSocketId)) || ''
                : stripQuotes(attr(node, 'TargetLang', ''));
            if (!text || !lang) return text || '';
            return await requestTranslation(String(text), String(lang));
        }

        /// Renders a string into an OffscreenCanvas using the configured font/size/color/alignment
        /// and returns it as the Image socket value (compatible with Display).
        async evalTextRender(node) {
            const textLink = this.findLinkTo(node.Id, 'Text');
            let text = textLink
                ? String((await this.evalNodeOutput(textLink.FromNodeId, textLink.FromSocketId)) ?? '')
                : stripQuotes(attr(node, 'Text', ''));
            // {Args1}..{ArgsN} positional substitution from triggerContext.eventData.
            // Hub's ScriptManager.ExpandArgsList split a Visual.Trigger Args="a,b,c"
            // into ed.Args1=a, ed.Args2=b, ed.Args3=c before delivery — so authors
            // can drop {Args1} into a plain text body and it lights up at render time.
            // Missing-arg case substitutes empty string and emits a once-per-fire
            // diagnostic so a typo'd {Args5} on a 3-arg trigger surfaces in logs.
            text = this.substituteArgs(node, text);
            if (!text) return null;

            // F4 / M62 — every styling input checks the wired link first, then falls back
            // to the inline attribute. Mirrors how the Text input above works so that any
            // styling socket can be driven from upstream Math/Vector/Color graphs.
            const fsLink = this.findLinkTo(node.Id, 'FontSize');
            let fontSize = parseFloat(attr(node, 'FontSize', '32')) || 32;
            if (fsLink) {
                const v = await this.evalNodeOutput(fsLink.FromNodeId, fsLink.FromSocketId);
                if (typeof v === 'number') fontSize = v;
            }

            const ffLink = this.findLinkTo(node.Id, 'FontFamily');
            let fontFam = stripQuotes(attr(node, 'FontFamily', '"Inter"'));
            if (ffLink) {
                const v = await this.evalNodeOutput(ffLink.FromNodeId, ffLink.FromSocketId);
                if (v != null) fontFam = String(v);
            }

            const colorLink = this.findLinkTo(node.Id, 'Color');
            let color = attrAnimatedColor(node, 'Color', '"#ffffff"');
            if (colorLink) {
                const v = await this.evalNodeOutput(colorLink.FromNodeId, colorLink.FromSocketId);
                if (v) color = colorToCss(v);
            }

            const alignLink = this.findLinkTo(node.Id, 'Alignment');
            let alignment = stripQuotes(attr(node, 'Alignment', '"center"'));
            if (alignLink) {
                const v = await this.evalNodeOutput(alignLink.FromNodeId, alignLink.FromSocketId);
                if (v != null) alignment = String(v);
            }

            // [text-stroke 2026-06-10] Optional outline. StrokeWidth is a raw
            // pixel width (0 = no outline); StrokeColor the outline colour. Same
            // wired-link-then-inline-attr resolution as every styling input above
            // so the outline can be driven from an upstream Math / Color graph.
            const strokeWidthLink = this.findLinkTo(node.Id, 'StrokeWidth');
            let strokeWidth = parseFloat(attr(node, 'StrokeWidth', '0')) || 0;
            if (strokeWidthLink) {
                const v = await this.evalNodeOutput(strokeWidthLink.FromNodeId, strokeWidthLink.FromSocketId);
                if (typeof v === 'number') strokeWidth = v;
            }
            const strokeColorLink = this.findLinkTo(node.Id, 'StrokeColor');
            let strokeColor = attrAnimatedColor(node, 'StrokeColor', '"#000000"');
            if (strokeColorLink) {
                const v = await this.evalNodeOutput(strokeColorLink.FromNodeId, strokeColorLink.FromSocketId);
                if (v) strokeColor = colorToCss(v);
            }

            // Optional background fill behind the text — same wired-link-then-attr
            // resolution as every styling input above. Default #00000000 is fully
            // transparent (no background box, so existing Text.Render nodes are
            // unchanged); any non-zero alpha paints a solid plate over the frame.
            const bgLink = this.findLinkTo(node.Id, 'Background');
            let background = attrAnimatedColor(node, 'Background', '"#00000000"');
            if (bgLink) {
                const v = await this.evalNodeOutput(bgLink.FromNodeId, bgLink.FromSocketId);
                if (v) background = colorToCss(v);
            }

            // M64 — make sure the FontFace is loaded BEFORE measuring. Without this, the
            // first render of a custom font measures with the system fallback's metrics
            // and the layout is off; once the real font swaps in the canvas is already
            // sized wrong. ensureFontLoaded() short-circuits via document.fonts.check()
            // when the face is already resident, so the steady-state cost is ~zero.
            await ensureFontLoaded(fontFam, fontSize);

            // Bug #2 — EXPORT FORMAT. Rasterize onto a WIDGET-FRAME-sized canvas at the
            // text's real font size instead of a tight bitmap. This makes Text.Render a
            // first-class frame image (like Image.Load), so EVERY downstream manipulation
            // node — Image.Transform / Crop / Mask / Blend … — composes with it in the
            // same widget-pixel space. The old tight bitmap was fit-and-centred to fill
            // the widget by the Display sink, which CANCELLED Image.Transform's translate
            // (the whole bitmap was re-centred) and mismatched Crop/Mask's widget-
            // normalised coordinates → "Text.Render can't be repositioned". The C#
            // design-time mirror (NodeEvaluator) already reports Text.Render at widget-rect
            // size, so frame-sizing the runtime brings the two into parity.
            //
            // The frame size is LOGICAL widget pixels — it matches the manipulator's
            // TranslateX/Y units 1:1 and every kernel's logical coordinate math. The text
            // is rendered at its real font size, positioned by Alignment, and is no longer
            // stretched to fill the widget (frame === render rect ⇒ Display draws ~1:1).
            // Sharpness through enlarged previews is handled by the supersampled BACKING
            // store below (density-preserving Transform/Blend carry it to Display).
            const frame = this.frame;
            let fw, fh;
            if (frame) {
                fw = Math.max(1, Math.round(frame.width));
                fh = Math.max(1, Math.round(frame.height));
            } else {
                // Legacy fallback (Evaluator built without a frame) — snug measured bitmap
                // so a Text.Render node still renders something sensible standalone.
                const probe = (typeof OffscreenCanvas !== 'undefined')
                    ? new OffscreenCanvas(8, 8)
                    : (() => { const cc = document.createElement('canvas'); cc.width = 8; cc.height = 8; return cc; })();
                const probeCtx = probe.getContext('2d');
                probeCtx.font = `${fontSize}px ${fontFam}`;
                fw = Math.max(8, Math.ceil(probeCtx.measureText(text).width)) + 16;
                fh = Math.ceil(fontSize * 1.4) + 8;
            }

            // Bug #1 (text blurry) — supersample the backing store (but report LOGICAL
            // fw/fh) so the frame text stays crisp when the small widget-rect preview
            // canvas is shown enlarged (the HiDPI / fit-to-pane upscale that softened it).
            // Image.Transform / Blend / Combine preserve this density to the Display sink,
            // which downsamples the dense bitmap → sharp. SS is capped so a large frame's
            // offscreen long edge stays sane; SS===1 (very large frames) is the prior path.
            const SS = Math.max(1, Math.min(3, Math.floor(4096 / Math.max(fw, fh, 1))));
            const off = (typeof OffscreenCanvas !== 'undefined')
                ? new OffscreenCanvas(fw * SS, fh * SS)
                : (() => { const cc = document.createElement('canvas'); cc.width = fw * SS; cc.height = fh * SS; return cc; })();
            const c = off.getContext('2d');
            c.scale(SS, SS);   // draw in LOGICAL frame coords; the backing store is SS× denser
            c.clearRect(0, 0, fw, fh);
            // Background plate first (logical frame coords), so the outline + fill
            // glyphs sit on top of it. Skipped when fully transparent.
            if (!_isTransparentCss(background)) {
                c.fillStyle = background;
                c.fillRect(0, 0, fw, fh);
            }
            c.font         = `${fontSize}px ${fontFam}`;
            c.fillStyle    = color;
            c.textBaseline = 'middle';
            c.textAlign    = alignment === 'left' ? 'left' : alignment === 'right' ? 'right' : 'center';
            // Horizontal: honour Alignment within the frame, inset off the edge for
            // left/right. Vertical: the row BLOCK is centred — Image.Transform's TranslateY
            // repositions it from there. Overflow clips at the frame edge (or the author
            // scales it down with an Image.Transform).
            const pad   = Math.max(4, Math.round(fontSize * 0.15));
            const textX = alignment === 'left' ? pad : alignment === 'right' ? (fw - pad) : (fw / 2);

            // ── V10 — MULTI-LINE, and why it is a repair rather than a feature ──────
            //
            // This block used to be a single fillText at fh/2. Canvas fillText does not break
            // lines, so any text containing a newline was already rendered WRONGLY: the rows
            // collapsed onto one baseline and everything after the first \n was effectively
            // lost. That was never theoretical — Loyalty.Leaderboard has always emitted its
            // ranks newline-joined, and every list-shaped member of the channel-fed widget
            // family (List.Live, an event list, a top-donator board, a viewer queue, end
            // credits) emits rows the same way. A multi-row readout could be produced by
            // existing nodes but not displayed by any of them.
            //
            // A ONE-row string takes the identical old path at the identical baseline
            // (rows.length === 1 ⇒ blockTop === fh / 2), so every saved single-line graph
            // renders byte-for-byte as before; only text that was already broken changes.
            const lhRaw = parseFloat(attr(node, 'LineHeight', '1.25'));
            const lineHeight = fontSize * (Number.isFinite(lhRaw) ? Math.max(0.5, Math.min(3, lhRaw)) : 1.25);
            let rows = String(text).split(/\r?\n/);
            // Wrap is OFF by default: wrapping silently changes the height of authored text,
            // and a PRE-BROKEN list wants its own rows honoured rather than re-flowed.
            if (this._readBool(node, 'Wrap')) {
                const limit = Math.max(1, fw - 2 * pad);
                const wrapped = [];
                for (const row of rows) for (const piece of _wrapTextRow(c, row, limit)) wrapped.push(piece);
                rows = wrapped;
            }
            const blockTop = fh / 2 - ((rows.length - 1) * lineHeight) / 2;
            // [text-stroke 2026-06-10] Draw the outline FIRST so the fill sits on
            // top of the inner half of the centred stroke — the standard crisp-
            // outline technique. lineJoin/miterLimit keep glyph corners rounded
            // instead of spiking. lineWidth is in LOGICAL px (we're inside the
            // c.scale(SS,SS) frame) so it matches the author's "width in px".
            // Per ROW, so a stroked multi-row list outlines every row.
            if (strokeWidth > 0) {
                c.lineJoin    = 'round';
                c.miterLimit  = 2;
                c.lineWidth   = strokeWidth;
                c.strokeStyle = strokeColor;
            }
            for (let i = 0; i < rows.length; i++) {
                const ly = blockTop + i * lineHeight;
                if (strokeWidth > 0) c.strokeText(rows[i], textX, ly);
                c.fillText(rows[i], textX, ly);
            }

            // Image-shaped result; width/height are the LOGICAL frame size, so the Display
            // sink and every downstream kernel keep using logical coordinates.
            return { image: off, width: fw, height: fh };
        }

        // ── F5 / H61 — Visual.OnStartup / OnTrigger event-data sources ──────

        /// Reads from the module-level `triggerContext`. Dispatches by socket name so the
        /// caller's `socketId` resolves to the correct field.
        evalVisualOnStartup(node, socketId) {
            const sock = node.Sockets && Array.isArray(node.Sockets) ? node.Sockets.find(s => s.Id === socketId) : null;
            if (!sock) return null;
            switch (sock.Name) {
                case 'LayerId':   return triggerContext.layerId || '';
                case 'Timestamp': return triggerContext.timestamp;
                case 'Flow':      return null;     // execution token; consumers don't read flow as data
                default:          return null;
            }
        }

        evalVisualOnTrigger(node, socketId) {
            const sock = node.Sockets && Array.isArray(node.Sockets) ? node.Sockets.find(s => s.Id === socketId) : null;
            if (!sock) return null;
            const ed = triggerContext.eventData || {};
            switch (sock.Name) {
                case 'TriggerName': return triggerContext.triggerName || '';
                case 'EventData':   return triggerContext.eventDataJson || '{}';
                case 'UserName':    return String(ed.user ?? ed.UserName ?? ed.userName ?? '');
                case 'Message':     return String(ed.message ?? ed.Message ?? '');
                case 'Flow':        return null;
                default:            return null;
            }
        }

        // Result.If — image barrier driven by triggerContext.eventData[When]. Returns
        // the upstream In image when the comparison matches, null otherwise so Display
        // sees no image flowing through this branch. Mirrors NodeEvaluator.EvalResultIf
        // on the C# side; both must agree on equality semantics (case-sensitive String
        // compare against the resolved Equals value).
        async evalResultIf(node) {
            const when      = stripQuotes(attr(node, 'When',   '""'));
            let   expectVal = stripQuotes(attr(node, 'Equals', '""'));
            const eqLink = this.findLinkTo(node.Id, 'Equals');
            if (eqLink) {
                const v = await this.evalNodeOutput(eqLink.FromNodeId, eqLink.FromSocketId);
                if (v != null) expectVal = String(v);
            }

            const ed = triggerContext.eventData || {};
            if (!when || !Object.prototype.hasOwnProperty.call(ed, when)) {
                this._reportMissingArg(node, when, 'result_if');
                return null;
            }
            if (String(ed[when]) !== String(expectVal)) return null;

            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) return null;
            return await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
        }

        // {Args1}..{ArgsN} positional substitution. Used by evalTextRender so authors
        // can drop "{Args1} won {Args2} bits" into a Text node body and have it
        // resolve at render time. Missing-arg cases substitute empty string and emit
        // a once-per-fire diagnostic — same dedup key shape as Result.If.
        substituteArgs(node, text) {
            if (!text || text.indexOf('{') === -1) return text;
            const ed = triggerContext.eventData || {};
            return String(text).replace(/\{(Args\d+)\}/g, (_, key) => {
                if (Object.prototype.hasOwnProperty.call(ed, key)) return String(ed[key] ?? '');
                this._reportMissingArg(node, key, 'text_substitution');
                return '';
            });
        }

        /// Reports a named event arg that was not supplied, once per (node, arg, kind) per PAGE —
        /// see _reportedMissingArgs for why the latch cannot live on the Evaluator. Both callers
        /// (Result.If's gate and {ArgsN} substitution) re-evaluate on every non-trigger render, so a
        /// per-render latch reported at frame rate, which sendEvalDiagnostic's contract forbids.
        _reportMissingArg(node, when, kind) {
            const key = `${node.Id}|${when}|${kind}`;
            if (_reportedMissingArgs.has(key)) return;
            _reportedMissingArgs.add(key);
            const code = kind === 'result_if' ? 'args_missing_branch' : 'args_missing_text';
            // Same shared frame builder as the media-path and malformed-list reports — it used to
            // be a third hand-rolled copy of the identical literal, which is how one of them ended
            // up console-only in the first place.
            sendEvalDiagnostic(code, `node=${node.Id} when='${when}'`, this.widgetId);
            try { console.warn(`[compositor] ${code}: node=${node.Id} when='${when}'`); }
            catch (_) { /* console disabled */ }
        }

        // ── C10 / F7 — Math + Vector kernels ────────────────────────────────

        /// Resolves a Scalar input. If linked, evaluates the upstream node and unwraps
        /// either a number or the X component of a Vector. Falls back to the inline
        /// attribute when no link is present.
        async _evalScalarSocket(node, name, fallback = 0) {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                if (typeof v === 'number') return v;
                if (v && typeof v.x === 'number') return v.x;
                return fallback;
            }
            const raw = parseFloat(attr(node, name, String(fallback)));
            return Number.isFinite(raw) ? raw : fallback;
        }

        /// Resolves a socket that may carry either a Scalar (number) or a Vector ({x,y}).
        /// Used by Math.* binary ops to support broadcast semantics.
        async _evalScalarOrVectorSocket(node, name) {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                if (typeof v === 'number') return v;
                if (v && typeof v.x === 'number') return v;
                return 0;
            }
            const raw = parseFloat(attr(node, name, '0'));
            return Number.isFinite(raw) ? raw : 0;
        }

        // L42 broadcast — Scalar→VectorN widening helpers. Type widening rules between
        // Scalar and the various VectorN sockets were previously undefined, so a
        // Scalar.Constant feeding (say) a Math.LerpVector3.A socket would silently
        // resolve to NaN. The convention is to broadcast the scalar across all
        // components: s → [s, s], [s, s, s], [s, s, s, s]. The C# evaluator's
        // ResolveVectorInputSocket mirrors this behaviour (see NodeEvaluator.cs
        // BroadcastScalarToVector) so design-time tests and OBS render agree.
        _broadcastToVector2(s) { return { x: s, y: s }; }
        _broadcastToVector3(s) { return { x: s, y: s, z: s }; }
        _broadcastToVector4(s) { return { x: s, y: s, z: s, w: s }; }

        /// Resolves a socket expected to carry a VectorN-shaped value (n=2/3/4).
        /// Walks the upstream link if present; broadcasts a scalar input to a vector
        /// of the requested width via the L42 broadcast helpers; falls back to
        /// per-component attribute parsing (X/Y[/Z[/W]]) when no link is wired.
        async _evalVectorSocket(node, name, n) {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                // L42 broadcast — promote scalar inputs to vectors of the right width.
                if (typeof v === 'number') {
                    if (n === 2) return this._broadcastToVector2(v);
                    if (n === 3) return this._broadcastToVector3(v);
                    return this._broadcastToVector4(v);
                }
                if (v && typeof v === 'object' && typeof v.x === 'number') {
                    // Pad missing components with 0 so a Vector2 wired into a Vector4 socket
                    // (legal — the editor's wildcard rules permit narrowing-with-zero-pad)
                    // still resolves to a usable shape.
                    const out = { x: v.x || 0, y: v.y || 0 };
                    if (n >= 3) out.z = (typeof v.z === 'number') ? v.z : 0;
                    if (n >= 4) out.w = (typeof v.w === 'number') ? v.w : 0;
                    return out;
                }
                // Anything else (null, string) → zero vector.
            }
            // Attribute fallback. Vector2 → X/Y; Vector3 → +Z; Vector4 → +W.
            const x = parseFloat(attr(node, 'X', '0')) || 0;
            const y = parseFloat(attr(node, 'Y', '0')) || 0;
            if (n === 2) return { x, y };
            const z = parseFloat(attr(node, 'Z', '0')) || 0;
            if (n === 3) return { x, y, z };
            const w = parseFloat(attr(node, 'W', '0')) || 0;
            return { x, y, z, w };
        }

        async evalMathBinary(node, op) {
            const a = await this._evalScalarOrVectorSocket(node, 'A');
            const b = await this._evalScalarOrVectorSocket(node, 'B');
            const aVec = a && typeof a === 'object' && 'x' in a;
            const bVec = b && typeof b === 'object' && 'x' in b;
            if (!aVec && !bVec) return op(a || 0, b || 0);
            // Broadcast — scalar input lifted to {x:s, y:s}, then component-wise op.
            const av = aVec ? a : { x: a || 0, y: a || 0 };
            const bv = bVec ? b : { x: b || 0, y: b || 0 };
            return { x: op(av.x, bv.x), y: op(av.y, bv.y) };
        }

        async evalMathLerp(node) {
            const a = await this._evalScalarOrVectorSocket(node, 'A');
            const b = await this._evalScalarOrVectorSocket(node, 'B');
            const t = await this._evalScalarSocket(node, 'T', 0);
            const aVec = a && typeof a === 'object' && 'x' in a;
            const bVec = b && typeof b === 'object' && 'x' in b;
            if (!aVec && !bVec) return (a || 0) + ((b || 0) - (a || 0)) * t;
            const av = aVec ? a : { x: a || 0, y: a || 0 };
            const bv = bVec ? b : { x: b || 0, y: b || 0 };
            return { x: av.x + (bv.x - av.x) * t, y: av.y + (bv.y - av.y) * t };
        }

        async evalMathClamp(node) {
            // Clamp is scalar-only per template (V/Min/Max all Scalar).
            const v  = await this._evalScalarSocket(node, 'V',   0);
            const mn = await this._evalScalarSocket(node, 'Min', 0);
            const mx = await this._evalScalarSocket(node, 'Max', 1);
            return Math.max(mn, Math.min(mx, v));
        }

        // F9 — layer dimensions are already in closure scope via the `layer` variable.
        evalMathResolution() {
            if (!layer || !layer.resolution) return { x: 0, y: 0 };
            return { x: layer.resolution.width || 0, y: layer.resolution.height || 0 };
        }

        // ── Track D — Math / Time / String / Convert / Message kernels ──────────
        //
        // Shared input-resolution helpers. _evalAnimScalarSocket mirrors
        // _evalScalarSocket but reads the inline attribute fallback through
        // attrAnimated() so the attr keyframes (matching how Image.Scale reads
        // "Factor"); the wired upstream value still wins when a link is present.
        // _evalStringSocket mirrors the linked-string pattern in evalTextTranslate:
        // wired String input overrides the plain (non-animated) attribute.

        /// Resolves a keyframeable Scalar input. Wired upstream value wins; otherwise
        /// the inline attribute is sampled at the current timeMs via attrAnimated.
        async _evalAnimScalarSocket(node, name, fallback = 0) {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                if (typeof v === 'number') return v;
                if (v && typeof v.x === 'number') return v.x;
                return fallback;
            }
            const raw = parseFloat(attrAnimated(node, name, String(fallback)));
            return Number.isFinite(raw) ? raw : fallback;
        }

        /// Resolves a String input. Wired upstream value (coerced to string) wins;
        /// otherwise the plain inline attribute (no keyframing for strings).
        async _evalStringSocket(node, name, fallback = '') {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                if (v != null) return String(v);
                return fallback;
            }
            return String(attr(node, name, fallback));
        }

        /// V7 — resolves a String input whose inline ATTRIBUTE is stored as a JSON-quoted
        /// literal. This is the resolver the dynamic media sources (Image.Load /
        /// Video.Load / Audio.Load "Path") and String.Select ("When") use.
        ///
        /// It is the exact mirror of NodeEvaluator.ResolveStringOrAttr, and it is a
        /// SEPARATE function from _evalStringSocket above for two reasons that are both
        /// bugs if you collapse them:
        ///
        ///   1. QUOTING. The Inspector commits String / Enum / MediaPath params as
        ///      JSON-quoted literals (NodeParamVm.CommitText), and every other reader of
        ///      Path unwraps them with stripQuotes. _evalStringSocket does NOT strip, so
        ///      routing Path through it would turn the stored "clip.mp3" into the 10-char
        ///      string including its quote marks and 404 every unwired graph in existence.
        ///      Only the ATTRIBUTE is stripped — a wired upstream value arrives already
        ///      unquoted (String.Constant, String.Select and friends all stripQuotes their
        ///      own literals), so stripping it again could eat a real leading/trailing
        ///      quote character out of somebody's filename.
        ///   2. WIRED-BUT-UNRESOLVED. _evalStringSocket returns its `fallback` ARGUMENT
        ///      when a link exists but yields null; the C# resolver falls back to the
        ///      node's own ATTRIBUTE instead. The C# behaviour is the one the sprint
        ///      contract specifies ("the attribute STAYS as the fallback"), and it is the
        ///      friendlier one: a dangling wire leaves the author's typed path working.
        ///      Note this is NOT reached when the upstream resolves to an empty string —
        ///      '' is not null, so a String.Select that matched nothing correctly yields
        ///      an empty path and the loader bails instead of silently playing the
        ///      attribute's clip.
        ///
        /// KNOWN divergence left alone on purpose: _evalStringSocket's un-stripped
        /// attribute read means String.Concat / Upper / Lower / Slice / Replace and the
        /// WebOverlay.Custom slots DO see quote marks in their inline attributes today,
        /// where the C# mirror does not. That is a real pre-existing bug across ~12 call
        /// sites with no test coverage on the browser side; it is reported rather than
        /// swept into this sprint, because changing it silently alters what authored
        /// graphs render.
        ///
        /// ── PROVENANCE ───────────────────────────────────────────────────────────────
        /// `provenance`, when supplied, is an out-parameter sink: the resolver stamps
        /// `provenance.wired = true` iff the returned value came from an UPSTREAM NODE, and
        /// false when it came from this node's own attribute. It reports where the value came
        /// FROM, not merely whether a link exists — a dangling wire that resolved to null
        /// falls back to the attribute and is therefore reported as an ATTRIBUTE value, which
        /// is what keeps the media-path guard below from punishing an author for a broken wire.
        ///
        /// An out-param rather than an object return so the resolver keeps ONE contract and
        /// ONE return shape for all three of its call sites: only the media loaders care about
        /// provenance, and String.Select's `When` (which is just a string) should not have to
        /// unwrap a tuple to get it.
        async _evalQuotedStringSocket(node, name, fallback = '', provenance = null) {
            const link = this.findLinkTo(node.Id, name);
            if (link) {
                const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
                if (v != null) {
                    if (provenance) provenance.wired = true;
                    return String(v);
                }
                // fall through to the attribute — mirrors ResolveStringOrAttr
            }
            if (provenance) provenance.wired = false;
            return stripQuotes(String(attr(node, name, fallback)));
        }

        /// THE media-path input resolver — the one chokepoint all three local-file loaders
        /// (Image.Load / Video.Load / Audio.Load) read their `Path` through, and the place the
        /// provenance rule is enforced. Mirrored exactly by NodeEvaluator.ResolveMediaPathInput
        /// so the design-time preview agrees with what OBS will fetch.
        ///
        /// ★ THE RULE, and it is deliberately NOT a blanket refusal:
        ///
        ///     An ATTRIBUTE value keeps today's behaviour exactly — a leading '/', an
        ///     http(s): URL and a data: URI all still pass straight through, because the
        ///     author who typed them into the Path box IS the streamer.
        ///
        ///     A WIRED value must be a RELATIVE path. A wired leading '/', http(s): or
        ///     data: string is rejected: the resolver reports a trigger diagnostic naming
        ///     the rejected value and returns '', so the loader's own empty-path check
        ///     bails cleanly (no element created, no fetch, no render).
        ///
        /// Why provenance and not a flat rule: V7 made Path wirable and its headline chain
        /// wires Visual.Arg — i.e. triggerContext.eventData, i.e. a chat argument — into it.
        /// A viewer typing `!sound https://attacker/x.mp3` into a command that forwards its
        /// text would otherwise make the streamer's OBS fetch an attacker-named URL: the
        /// streamer's home IP disclosed to the attacker, arbitrary media on air, and a data:
        /// URL rendering attacker content inline. The escape hatches are for the author, so
        /// they are gated on authorship rather than removed. See isNonRelativeMediaPath for
        /// the single shared definition of "non-relative" (the same predicate resolveMediaPath
        /// uses for its pass-through, so the two sets cannot drift apart).
        ///
        /// A rejected path returns '' rather than falling back to the attribute. Falling back
        /// would play the author's leftover clip in response to attacker input, which is a
        /// quieter version of the same problem — and it would mask the diagnostic.
        async _evalMediaPathSocket(node) {
            const provenance = { wired: false };
            const path = await this._evalQuotedStringSocket(node, 'Path', '', provenance);
            if (!path) return '';
            // Author-typed: unchanged behaviour, absolute paths and URLs included.
            if (!provenance.wired) return path;
            if (!isNonRelativeMediaPath(path)) return path;
            this._reportRejectedMediaPath(node, path);
            return '';
        }

        /// Reports a wired media path that failed the relative-path rule, once per
        /// (node, value) per page — see _reportedRejectedMediaPaths for why the dedupe has to be
        /// page-level rather than per-Evaluator (an Evaluator is built per render, so a latch on it
        /// resets every animator frame).
        ///
        /// The value IS named in the detail, on purpose: without it the streamer sees "a path
        /// was rejected" and has no way to tell a typo in their own graph from somebody
        /// probing their overlay. The frame itself is built by sendEvalDiagnostic, which
        /// truncates to 240 characters, so a megabyte-long data: URI cannot flood the Hub log.
        _reportRejectedMediaPath(node, value) {
            const key = `${node.Id}|${value}`;
            if (_reportedRejectedMediaPaths.has(key)) return;
            _reportedRejectedMediaPaths.add(key);
            const detail = `node=${node.Id} title=${node.Title} path='${String(value).slice(0, 160)}'`;
            sendEvalDiagnostic('media_path_not_relative', detail, this.widgetId);
            try {
                console.warn('[compositor] media_path_not_relative: a WIRED media path must be '
                           + 'relative to data/media — absolute paths, http(s): URLs and data: '
                           + 'URIs are refused because a wired path can carry viewer input. '
                           + detail);
            } catch (_) { /* console disabled */ }
        }

        // Numeric Math — unary (V or Degrees) and binary (A,B) scalar kernels.
        async evalMathUnary(node, name, op) {
            return op(await this._evalAnimScalarSocket(node, name, 0));
        }

        async evalMathMod(node) {
            const a = await this._evalAnimScalarSocket(node, 'A', 0);
            const b = await this._evalAnimScalarSocket(node, 'B', 1);
            return b === 0 ? 0 : a - b * Math.floor(a / b);
        }

        async evalMathPow(node) {
            const base = await this._evalAnimScalarSocket(node, 'Base', 1);
            const exp  = await this._evalAnimScalarSocket(node, 'Exp',  2);
            return Math.pow(base, exp);
        }

        async evalMathMin(node) {
            const a = await this._evalAnimScalarSocket(node, 'A', 0);
            const b = await this._evalAnimScalarSocket(node, 'B', 0);
            return Math.min(a, b);
        }

        async evalMathMax(node) {
            const a = await this._evalAnimScalarSocket(node, 'A', 0);
            const b = await this._evalAnimScalarSocket(node, 'B', 0);
            return Math.max(a, b);
        }

        async evalMathRemap(node) {
            const v      = await this._evalAnimScalarSocket(node, 'V',      0);
            const inMin  = await this._evalAnimScalarSocket(node, 'InMin',  0);
            const inMax  = await this._evalAnimScalarSocket(node, 'InMax',  1);
            const outMin = await this._evalAnimScalarSocket(node, 'OutMin', 0);
            const outMax = await this._evalAnimScalarSocket(node, 'OutMax', 1);
            const d = inMax - inMin;
            const t = d === 0 ? 0 : (v - inMin) / d;
            return outMin + t * (outMax - outMin);
        }

        // Math.Compare — returns 1.0 when the comparison holds, else 0.0. Mode is an
        // enum attribute (plain attr); Equal/NotEqual use a small epsilon.
        async evalMathCompare(node) {
            const a = await this._evalAnimScalarSocket(node, 'A', 0);
            const b = await this._evalAnimScalarSocket(node, 'B', 0);
            const mode = stripQuotes(attr(node, 'Mode', 'GreaterThan'));
            const EPS = 1e-6;
            let result;
            switch (mode) {
                case 'GreaterThan':    result = a > b;  break;
                case 'LessThan':       result = a < b;  break;
                case 'GreaterOrEqual': result = a >= b; break;
                case 'LessOrEqual':    result = a <= b; break;
                case 'Equal':          result = Math.abs(a - b) <= EPS; break;
                case 'NotEqual':       result = Math.abs(a - b) > EPS;  break;
                default:               result = a > b;  break;
            }
            return result ? 1.0 : 0.0;
        }

        // Time / animation — timeMs is in milliseconds; convert to seconds.
        //
        // V5 made this real: a production render's triggerContext.timeMs ADVANCES from the
        // widget's last activation (the global animator loop writes `now - activationStart`
        // immediately before each frame), so an Oscillator on stream actually oscillates. It used
        // to stay 0 forever in production, which is why these three read as "resolve to their
        // start value" in older comments — that was the defect, not the contract.
        //
        // The C# mirror in NodeEvaluator has NOT changed here and deliberately does not match:
        // its Time.Elapsed / Oscillator / Sawtooth arms are hard-coded to t = 0 because that
        // mirror has no clock of its own to sample. What V11 threads into it (EvalContext.TimeMs,
        // the editor's playhead) reaches the KEYFRAME sampler — KeyframeInterpolation.SampleScalar
        // — not these kernels. So keyframed attributes agree between C# and JS at a given cursor;
        // a graph whose motion comes from a Time.* node animates in OBS and sits at its origin in
        // a design-time thumbnail. Anyone chasing "mirror drift" should check that boundary first.
        evalTimeElapsed() {
            return (triggerContext.timeMs || 0) / 1000;
        }

        async evalTimeOscillator(node) {
            const freq   = await this._evalAnimScalarSocket(node, 'Frequency', 1);
            const amp    = await this._evalAnimScalarSocket(node, 'Amplitude', 1);
            const phase  = await this._evalAnimScalarSocket(node, 'Phase',     0);
            const offset = await this._evalAnimScalarSocket(node, 'Offset',    0);
            const t = (triggerContext.timeMs || 0) / 1000;
            return offset + amp * Math.sin(2 * Math.PI * (freq * t + phase));
        }

        async evalTimeSawtooth(node) {
            const period = await this._evalAnimScalarSocket(node, 'Period', 1);
            const t = (triggerContext.timeMs || 0) / 1000;
            const p = Math.max(period, 1e-6);
            return (t % p) / p;
        }

        // Time.Easing — clamp01(T) through the easing curve. Mirrors
        // KeyframeInterpolation.ApplyCurve (and applyCurveKf above) so design-time and
        // OBS agree; Mode is an enum attribute (plain attr).
        async evalTimeEasing(node) {
            const t = clamp(await this._evalAnimScalarSocket(node, 'T', 0), 0, 1);
            // Folded the same way applyCurveKf now folds its keyframe token. Mode arrives from
            // node.Attributes (authored PascalCase) rather than the enum serializer, so this
            // site was not broken — but the two dispatchers must stay one shape, and folding
            // makes this one tolerant of a camelCase Mode written by any future producer.
            const rawMode = stripQuotes(attr(node, 'Mode', 'easeInOut'));
            const mode = typeof rawMode === 'string' ? rawMode.toLowerCase() : rawMode;
            switch (mode) {
                case 'linear':    return t;
                case 'easein':    return t * t;
                case 'easeout':   return 1 - (1 - t) * (1 - t);
                case 'easeinout': return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
                case 'step':      return 0;
                default:          return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            }
        }

        // String — A+B / case / length / slice / replace-all. String/enum attrs use
        // plain attr(); wired String inputs override via _evalStringSocket.
        async evalStringConcat(node) {
            const a = await this._evalStringSocket(node, 'A', '');
            const b = await this._evalStringSocket(node, 'B', '');
            return a + b;
        }

        async evalStringUpper(node) {
            return (await this._evalStringSocket(node, 'In', '')).toUpperCase();
        }

        async evalStringLower(node) {
            return (await this._evalStringSocket(node, 'In', '')).toLowerCase();
        }

        async evalStringLength(node) {
            return (await this._evalStringSocket(node, 'In', '')).length;
        }

        async evalStringSlice(node) {
            const s     = await this._evalStringSocket(node, 'In', '');
            const start = await this._evalScalarSocket(node, 'Start', 0);
            const count = await this._evalScalarSocket(node, 'Count', -1);
            const len = s.length;
            const st  = clamp(Math.trunc(start), 0, len);
            const n   = count < 0 ? len - st : Math.trunc(count);
            return s.substring(st, st + Math.max(0, n));
        }

        async evalStringReplace(node) {
            const s    = await this._evalStringSocket(node, 'In',   '');
            const find = await this._evalStringSocket(node, 'Find', '');
            const wth  = await this._evalStringSocket(node, 'With', '');
            if (find === '') return s;
            return s.split(find).join(wth);
        }

        // Convert — scalar↔string, RGBA→Color, hex→Color. Color outputs are emitted as
        // #rrggbbaa hex strings (the spec's canonical form); colorToCss/parseHexColor
        // downstream accept either the hex string or the {r,g,b,a} object form.
        async evalConvertNumberToString(node) {
            const v = await this._evalAnimScalarSocket(node, 'V', 0);
            let decimals = parseInt(attr(node, 'Decimals', '0'), 10);
            if (!Number.isFinite(decimals)) decimals = 0;
            decimals = clamp(decimals, 0, 6);
            return v.toFixed(decimals);
        }

        async evalConvertStringToNumber(node) {
            const raw = parseFloat(await this._evalStringSocket(node, 'In', ''));
            return Number.isFinite(raw) ? raw : 0;
        }

        async evalConvertColorFromRGBA(node) {
            const r = clamp(Math.round(await this._evalAnimScalarSocket(node, 'R', 255)), 0, 255);
            const g = clamp(Math.round(await this._evalAnimScalarSocket(node, 'G', 255)), 0, 255);
            const b = clamp(Math.round(await this._evalAnimScalarSocket(node, 'B', 255)), 0, 255);
            const a = clamp(Math.round(await this._evalAnimScalarSocket(node, 'A', 255)), 0, 255);
            const hex2 = n => n.toString(16).padStart(2, '0');
            return `#${hex2(r)}${hex2(g)}${hex2(b)}${hex2(a)}`;
        }

        async evalConvertHexToColor(node) {
            const hex = String(await this._evalStringSocket(node, 'Hex', '#ffffff')).trim();
            let s = hex.startsWith('#') ? hex.slice(1) : hex;
            if (s.length === 3) s = s.split('').map(c => c + c).join('');
            if ((s.length === 6 || s.length === 8) && /^[0-9a-fA-F]+$/.test(s)) {
                return `#${s.toLowerCase()}`;
            }
            return '#ffffff';
        }

        // Message.Read — the read-out node for the transmitted message. Reads
        // triggerContext.eventData[Key]; falls back to the MockValue attribute when
        // the key is absent (the C# mirror returns MockValue directly so the canvas
        // preview is not blind to the transmitted string). Pure-data String producer.
        evalMessageRead(node) {
            const key  = stripQuotes(attr(node, 'Key',       'Args1'));
            const mock = stripQuotes(attr(node, 'MockValue', ''));
            const ed = triggerContext && triggerContext.eventData;
            return (ed && ed[key] != null) ? String(ed[key]) : mock;
        }

        // ── V7 — Visual.Arg: one named field of the trigger payload, as a String ──
        //
        // Same eventData read as evalMessageRead directly above, with ONE deliberate
        // difference that is the reason the node exists: the placeholder goes through
        // liveMock, so it reaches a canvas only on a design-time surface. Message.Read's
        // MockValue renders IN PRODUCTION, which is the fake-data-on-stream class this
        // whole rework removed (the shipped leaderboard placeholder painting invented
        // viewer names onto a live stream after every OBS scene return). Here an
        // unsupplied field renders NOTHING on air, exactly like Var.Live and every
        // channel reader.
        //
        // liveMock is used rather than an inline attr read on purpose: it is THE single
        // place in this file allowed to touch PreviewText, so there is exactly one
        // surface gate to get right and one place to audit. A reader that reached for the
        // attribute itself would bypass that gate — BugFixSweep3 pins the count at one.
        //
        // No missing-arg diagnostic, unlike Result.If and substituteArgs, and that is a
        // decision rather than an omission. A missing field is the NORMAL state outside a
        // trigger fire: every onStartup render, every live patch and every animator frame
        // re-evaluates this node with triggerContext.eventData reset to {}, so "missing" is
        // not evidence of an authoring mistake here the way it is inside a Result.If gate.
        // A report would therefore be wrong, not merely noisy.
        //
        // (The frame-rate hazard that used to be the second half of this argument is gone:
        // both existing reporters now latch on the PAGE-scoped _reportedMissingArgs rather
        // than on the Evaluator instance, so neither fires per render any more. The reason
        // above is the one that still stands, and it stands on its own.)
        evalVisualArg(node) {
            const key = stripQuotes(attr(node, 'Key', 'Args1')).trim();
            const ed  = triggerContext && triggerContext.eventData;
            if (key && ed && Object.prototype.hasOwnProperty.call(ed, key) && ed[key] != null)
                return String(ed[key]);
            return liveMock(node);
        }

        // ── V7 — String.Select: N-way string mapping with a mandatory default ──
        //
        // Byte-identical to the NodeEvaluator arm of the same name. First Case row whose
        // text EXACTLY equals the selector wins and the node emits that row's Value;
        // nothing matched emits Default. Three rules, each load-bearing:
        //   • Ordinal case-SENSITIVE compare, matching the Result.If gate. Case-insensitive
        //     was rejected because JS toLowerCase() and .NET OrdinalIgnoreCase do not agree
        //     on every Unicode input, and the browser and the design-time mirror picking
        //     different rows is the worst failure this node could have.
        //   • An EMPTY Case is an unconfigured row and is skipped. Without that, an empty
        //     selector — the normal state on an onStartup render — would match the first
        //     blank row and emit its Value, so a freshly dropped node would look as though
        //     it had chosen row 1.
        //   • Default is a real row: the Alerts tool labels an unmapped family
        //     generically, so a value nobody mapped genuinely arrives, and a select with
        //     no default would render nothing at all for it.
        // Rows are ATTRIBUTES, not sockets (the Logic.Switch precedent), so a node
        // hand-authored with fewer rows behaves identically — a missing Case reads ''
        // and is skipped.
        async evalStringSelect(node) {
            const selector = await this._evalQuotedStringSocket(node, 'When', '');
            for (let row = 1; row <= STRING_SELECT_ROWS; row++) {
                const caseText = stripQuotes(String(attr(node, `Case${row}`, '')));
                if (!caseText) continue;
                if (caseText !== selector) continue;
                return stripQuotes(String(attr(node, `Value${row}`, '')));
            }
            return stripQuotes(String(attr(node, 'Default', '')));
        }

        async evalVectorSplit(node, socketId) {
            const link = this.findLinkTo(node.Id, 'V');
            if (!link) return 0;
            const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
            if (v == null) return 0;
            const sock = node.Sockets && Array.isArray(node.Sockets) ? node.Sockets.find(s => s.Id === socketId) : null;
            if (!sock) return 0;
            if (sock.Name === 'X') return typeof v === 'number' ? v : (v.x || 0);
            if (sock.Name === 'Y') return typeof v === 'number' ? v : (v.y || 0);
            return 0;
        }

        async evalVectorCombine(node) {
            const x = await this._evalScalarSocket(node, 'X', 0);
            const y = await this._evalScalarSocket(node, 'Y', 0);
            return { x, y };
        }

        // M56 / F12 — Math.LerpVectorN. Component-wise lerp on Vector{2,3,4} inputs.
        // T is always Scalar; A/B accept any vector or a scalar (broadcast via L42).
        async evalMathLerpVectorN(node, n) {
            const a = await this._evalVectorSocket(node, 'A', n);
            const b = await this._evalVectorSocket(node, 'B', n);
            const t = await this._evalScalarSocket(node, 'T', 0);
            const out = {
                x: a.x + (b.x - a.x) * t,
                y: a.y + (b.y - a.y) * t,
            };
            if (n >= 3) out.z = (a.z || 0) + (((b.z || 0) - (a.z || 0)) * t);
            if (n >= 4) out.w = (a.w || 0) + (((b.w || 0) - (a.w || 0)) * t);
            return out;
        }

        // M56 / F12 — Vector{3,4}.Split. Mirrors the Vector2.Split pattern: emits one
        // Scalar per axis, dispatched by socket name. Unknown axes return 0 (matches
        // the legacy Vector.Split behavior on a missing socket).
        async evalVectorNSplit(node, socketId, n) {
            const link = this.findLinkTo(node.Id, 'V');
            if (!link) return 0;
            const v = await this.evalNodeOutput(link.FromNodeId, link.FromSocketId);
            if (v == null) return 0;
            const sock = node.Sockets && Array.isArray(node.Sockets) ? node.Sockets.find(s => s.Id === socketId) : null;
            if (!sock) return 0;
            // Allow scalar inputs to broadcast — same convention as L42 elsewhere.
            if (typeof v === 'number') {
                if (sock.Name === 'X' || sock.Name === 'Y') return v;
                if (n >= 3 && sock.Name === 'Z')           return v;
                if (n >= 4 && sock.Name === 'W')           return v;
                return 0;
            }
            if (sock.Name === 'X')             return v.x || 0;
            if (sock.Name === 'Y')             return v.y || 0;
            if (n >= 3 && sock.Name === 'Z')   return v.z || 0;
            if (n >= 4 && sock.Name === 'W')   return v.w || 0;
            return 0;
        }

        async evalVectorNCombine(node, n) {
            const x = await this._evalScalarSocket(node, 'X', 0);
            const y = await this._evalScalarSocket(node, 'Y', 0);
            if (n === 2) return { x, y };
            const z = await this._evalScalarSocket(node, 'Z', 0);
            if (n === 3) return { x, y, z };
            const w = await this._evalScalarSocket(node, 'W', 0);
            return { x, y, z, w };
        }

        // ── H65 / F14 — Viewer passthrough ──────────────────────────────────
        // The transparent passthrough IS the intended runtime behavior, not a
        // stopgap: this canvas is composited straight into the live OBS output,
        // so any on-canvas thumbnail render would be visible on stream. The
        // manifesto's Viewer thumbnail is a design-time affordance and ships in
        // Visualist (upstream-image preview on the node body). If a runtime
        // debug render is ever wanted it must ride the ?debug=1 opt-in like the
        // render telemetry — never the default path. Passing the input through
        // untouched keeps a Viewer dropped onto a wire from breaking the chain.
        async evalViewer(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) return null;
            return await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
        }

        // ── C10 / F7 — Image kernel implementations (Canvas2D) ─────────────
        // All four kernels follow the same pattern as Image.Crop / Image.Transform:
        //   1. Resolve required upstream image input.
        //   2. Allocate an offscreen canvas the size of the source image.
        //   3. Apply the kernel via Canvas2D APIs (filter / globalCompositeOperation
        //      / createPattern).
        //   4. Return { image: <canvas>, width, height } so downstream nodes treat
        //      the result like any other image.
        // Per-render memoisation in evalNodeOutput means a kernel feeding two
        // downstream branches is computed once.

        /// Image.ColorAdjust — Brightness / Contrast / Saturation in -1..1 range,
        /// Hue in degrees. Implemented via canvas `filter`. The source image is
        /// drawn through the filter onto an offscreen canvas the same size as the
        /// source — no scaling, just a colour transform pass.
        async evalImageColorAdjust(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.ColorAdjust: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            // Brightness/Contrast/Saturation are 0-centered (-1..1). CSS filter
            // brightness() / contrast() / saturate() are 1-centered (0=black,
            // 1=identity, 2=double), so map: css = 1 + value, clamped >= 0.
            const b = await this._evalScalarSocket(node, 'Brightness', parseFloat(attr(node, 'Brightness', '0')) || 0);
            const c = await this._evalScalarSocket(node, 'Contrast',   parseFloat(attr(node, 'Contrast',   '0')) || 0);
            const s = await this._evalScalarSocket(node, 'Saturation', parseFloat(attr(node, 'Saturation', '0')) || 0);
            const h = await this._evalScalarSocket(node, 'Hue',        parseFloat(attr(node, 'Hue',        '0')) || 0);

            const cssBright = Math.max(0, 1 + b);
            const cssContr  = Math.max(0, 1 + c);
            const cssSat    = Math.max(0, 1 + s);

            const w = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.filter = `brightness(${cssBright}) contrast(${cssContr}) saturate(${cssSat}) hue-rotate(${h}deg)`;
            octx.drawImage(upstream.image, 0, 0, w, ih);
            return { image: off, width: w, height: ih };
        }

        /// Image.Blur — single-attribute Gaussian blur via the canvas `filter`
        /// pipeline. Radius=0 short-circuits to a passthrough (no offscreen
        /// allocation) so authors who haven't keyframed the value yet don't
        /// pay the per-frame cost. Mirrors evalImageColorAdjust's structure.
        async evalImageBlur(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Blur: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const radius = await this._evalScalarSocket(node, 'Radius', parseFloat(attr(node, 'Radius', '0')) || 0);
            const r = Math.max(0, radius);
            if (r === 0) return upstream;

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.filter = `blur(${r}px)`;
            octx.drawImage(upstream.image, 0, 0, w, ih);
            return { image: off, width: w, height: ih };
        }

        /// Image.Gaussian — directional Gaussian blur via SVG `feGaussianBlur`.
        /// Distinct from Image.Blur: canvas2D `filter: blur()` is isotropic
        /// (one radius for both axes). feGaussianBlur's `stdDeviation` accepts
        /// two values, giving authors an X / Y axis sigma split for motion-
        /// blur style directional smearing. Both sigmas at 0 short-circuits
        /// to passthrough.
        ///
        /// The render path serialises the upstream into a data: URL, embeds
        /// it in an inline SVG that references the filter, then draws the
        /// rendered SVG back to a canvas via Image. SVG parsing is async, so
        /// the kernel awaits onload before composing the offscreen result.
        async evalImageGaussian(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Gaussian: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const sx = Math.max(0, await this._evalScalarSocket(node, 'SigmaX', parseFloat(attr(node, 'SigmaX', '0')) || 0));
            const sy = Math.max(0, await this._evalScalarSocket(node, 'SigmaY', parseFloat(attr(node, 'SigmaY', '0')) || 0));
            if (sx === 0 && sy === 0) return upstream;

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;

            // Step 1: serialise upstream image to a data URL the SVG can reference.
            // Sprint 74 — pooled. The canvas is discarded once the data URL is
            // captured; nothing downstream sees it.
            const tmp = canvasPool.acquire(w, ih);
            tmp.getContext('2d').drawImage(upstream.image, 0, 0, w, ih);
            const dataUrl = tmp.toDataURL();
            canvasPool.release(tmp);

            // Step 2: build an SVG that applies feGaussianBlur to that image.
            // The filter region is widened by 2σ on each side so the blurred
            // edges aren't clipped by the default 10% padding box.
            const padX = Math.ceil(sx * 2);
            const padY = Math.ceil(sy * 2);
            const svg =
                `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${ih}">` +
                  `<defs><filter id="g" x="${-padX}" y="${-padY}" width="${w + 2 * padX}" height="${ih + 2 * padY}" filterUnits="userSpaceOnUse">` +
                    `<feGaussianBlur stdDeviation="${sx} ${sy}"/>` +
                  `</filter></defs>` +
                  `<image href="${dataUrl}" width="${w}" height="${ih}" filter="url(#g)"/>` +
                `</svg>`;
            const svgUrl = 'data:image/svg+xml;utf8,' + encodeURIComponent(svg);

            // Step 3: rasterise the SVG via Image.onload, then drawImage onto
            // a fresh canvas so downstream consumers get a normal canvas like
            // every other Image.* kernel returns.
            const img = new Image();
            await new Promise((resolve, reject) => {
                img.onload  = resolve;
                img.onerror = () => reject(new Error('Image.Gaussian SVG rasterise failed'));
                img.src = svgUrl;
            });

            const off = this.acquireEscape(w, ih);
            off.getContext('2d').drawImage(img, 0, 0, w, ih);
            return { image: off, width: w, height: ih };
        }

        /// Image.Mosaic — pixelation via downscale + nearest-neighbour upscale.
        /// Step 1: drawImage(upstream) into a tiny canvas of size (W/N, H/N)
        /// with smoothing OFF, so each tile collapses to a single sample.
        /// Step 2: drawImage that tiny canvas back to a (W, H) canvas, again
        /// with smoothing OFF, so each sample expands into an N×N square.
        /// TileSize=1 short-circuits to passthrough (mosaic with one-pixel
        /// tiles is identity); TileSize is clamped >=1 to avoid div-by-zero.
        async evalImageMosaic(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Mosaic: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const tileRaw = await this._evalScalarSocket(node, 'TileSize', parseFloat(attr(node, 'TileSize', '8')) || 8);
            const tile = Math.max(1, Math.round(tileRaw));
            if (tile === 1) return upstream;

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;

            // Step 1: shrink to (W/tile, H/tile) — at least 1px each side so tiny
            // sources still produce a valid intermediate canvas. Sprint 74 —
            // `small` is pooled; consumed by step 2 and not returned.
            const sw = Math.max(1, Math.floor(w  / tile));
            const sh = Math.max(1, Math.floor(ih / tile));
            const small = canvasPool.acquire(sw, sh);
            const sctx = small.getContext('2d');
            sctx.imageSmoothingEnabled = false;
            sctx.drawImage(upstream.image, 0, 0, sw, sh);

            // Step 2: scale that back to the source size, again with smoothing
            // off, producing the chunky tiled look.
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.imageSmoothingEnabled = false;
            octx.drawImage(small, 0, 0, sw, sh, 0, 0, w, ih);
            canvasPool.release(small);
            return { image: off, width: w, height: ih };
        }

        /// Image.Shadow — drop-shadow via canvas2D `filter: drop-shadow()`.
        /// Drawing the upstream onto a fresh canvas through that filter draws
        /// both the source AND its colored, blurred, offset silhouette in one
        /// pass. Output canvas is the same size as the source — shadow that
        /// falls outside the source rect gets clipped (CSS box-shadow semantics
        /// for an in-rect compositor pipeline; chain Image.Transform first if
        /// you need overflow). Color is a CSS literal like "rgba(0,0,0,0.5)";
        /// stripQuotes peels the JSON-encoded default. Passthrough only when
        /// every numeric attr is exactly 0.
        async evalImageShadow(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Shadow: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const ox = await this._evalScalarSocket(node, 'OffsetX', parseFloat(attr(node, 'OffsetX', '4')) || 0);
            const oy = await this._evalScalarSocket(node, 'OffsetY', parseFloat(attr(node, 'OffsetY', '4')) || 0);
            const br = Math.max(0, await this._evalScalarSocket(node, 'Blur', parseFloat(attr(node, 'Blur', '6')) || 0));
            if (ox === 0 && oy === 0 && br === 0) return upstream;

            const color = attrAnimatedColor(node, 'Color', 'rgba(0,0,0,0.5)');

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.filter = `drop-shadow(${ox}px ${oy}px ${br}px ${color})`;
            octx.drawImage(upstream.image, 0, 0, w, ih);
            return { image: off, width: w, height: ih };
        }

        /// Image.Glow — outer glow via stacked zero-offset drop-shadows. Each
        /// pass applies `filter: drop-shadow(0 0 <Radius>px <Color>)` and
        /// composes onto the running canvas. Stacking N passes builds up an
        /// N-step brighter halo without losing alpha to a single overly-bright
        /// pass. Intensity is clamped to integer 0..4; 0 short-circuits to
        /// passthrough, as does Radius=0.
        async evalImageGlow(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Glow: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const radius = Math.max(0, await this._evalScalarSocket(node, 'Radius', parseFloat(attr(node, 'Radius', '12')) || 0));
            const intensityRaw = await this._evalScalarSocket(node, 'Intensity', parseFloat(attr(node, 'Intensity', '1')) || 0);
            const passes = Math.max(0, Math.min(4, Math.round(intensityRaw)));
            if (radius === 0 || passes === 0) return upstream;

            const color = attrAnimatedColor(node, 'Color', 'rgba(255,255,200,0.85)');

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');

            // First pass writes the glowed source. Subsequent passes redraw
            // the previous frame through the same filter, accumulating halo
            // brightness while keeping the original sprite crisp.
            octx.filter = `drop-shadow(0 0 ${radius}px ${color})`;
            octx.drawImage(upstream.image, 0, 0, w, ih);
            // Sprint 71 — `tmp` is a per-iteration scratch buffer; nothing
            // downstream sees it, so we recycle it through the pool. `off`
            // (above) is returned via value.image to the next kernel and
            // CANNOT be pooled.
            for (let i = 1; i < passes; i++) {
                const tmp = canvasPool.acquire(w, ih);
                const tctx = tmp.getContext('2d');
                tctx.drawImage(off, 0, 0, w, ih);
                octx.clearRect(0, 0, w, ih);
                octx.drawImage(tmp, 0, 0, w, ih);
                canvasPool.release(tmp);
            }
            return { image: off, width: w, height: ih };
        }

        /// Image.Distort — geometric distortion via per-row drawImage slicing.
        /// Mode "wave"   shifts each scanline horizontally by Amplitude * sin(...).
        /// Mode "ripple" adds the same vertical bow on top of the wave.
        /// Implementation copies the source row-by-row to an offscreen canvas
        /// using the 9-arg drawImage(srcCanvas, sx, sy, sw, 1, dx, dy, sw, 1)
        /// form — fast (no per-pixel ImageData read) and pixel-perfect because
        /// the source is already a canvas. Amplitude=0 short-circuits to
        /// passthrough. Unknown mode falls back to "wave" with a console.warn.
        async evalImageDistort(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Distort: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const amp  = await this._evalScalarSocket(node, 'Amplitude', parseFloat(attr(node, 'Amplitude', '8')) || 0);
            const freq = Math.max(0, await this._evalScalarSocket(node, 'Frequency', parseFloat(attr(node, 'Frequency', '4')) || 0));
            if (amp === 0) return upstream;

            let mode = stripQuotes(attr(node, 'Mode', 'wave')).toLowerCase();
            if (mode !== 'wave' && mode !== 'ripple') {
                console.warn(`[Visualist] Image.Distort: unknown Mode '${mode}' — falling back to 'wave'`);
                mode = 'wave';
            }

            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;

            // Stage the source as a canvas so drawImage's 9-arg slicing form
            // works regardless of whether upstream.image is an HTMLImageElement
            // or a canvas already. Sprint 83 — `src` is pooled (drawn-then-read
            // row-by-row, never returned to a downstream consumer). `off` keeps
            // its document.createElement allocation because it escapes via
            // value.image to the next kernel.
            const src = canvasPool.acquire(w, ih);
            src.getContext('2d').drawImage(upstream.image, 0, 0, w, ih);

            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');

            const twoPiF = (2 * Math.PI * freq);
            const denomIh = Math.max(1, ih);   // loop-invariant; keep as divisor (byte-identical FP)
            for (let y = 0; y < ih; y++) {
                const dx = amp * Math.sin(twoPiF * y / denomIh);
                octx.drawImage(src, 0, y, w, 1, dx, y, w, 1);
            }
            if (mode === 'ripple') {
                // Second pass: re-stage the wave-shifted output and bow it
                // along Y by sampling each column with a sin offset. Sprint 83 —
                // `stage` is also pooled (read column-by-column, never returned).
                const stage = canvasPool.acquire(w, ih);
                stage.getContext('2d').drawImage(off, 0, 0, w, ih);
                octx.clearRect(0, 0, w, ih);
                const denomW = Math.max(1, w);   // loop-invariant; keep as divisor (byte-identical FP)
                for (let x = 0; x < w; x++) {
                    const dy = amp * Math.sin(twoPiF * x / denomW);
                    octx.drawImage(stage, x, 0, 1, ih, x, dy, 1, ih);
                }
                canvasPool.release(stage);
            }
            canvasPool.release(src);
            return { image: off, width: w, height: ih };
        }

        /// Image.Mask — composites a Mask image over the source.
        ///
        /// L40 — alpha-compositing semantics are now explicit on the Mode attribute:
        ///   • mode = "alpha"     (default) — use the mask image's alpha channel as
        ///                                    the new alpha. Opaque areas of the mask
        ///                                    keep the source visible; transparent areas
        ///                                    erase it. Implemented via a single
        ///                                    `destination-in` composite of the raw mask.
        ///   • mode = "luminance"           — derive alpha from the mask's luminance
        ///                                    (grey = 50% alpha, white = opaque, black =
        ///                                    transparent). Implemented by first running
        ///                                    the mask through a `grayscale(1)` filter on
        ///                                    a scratch canvas, then `destination-in`.
        /// Unknown values fall back to "alpha" with a console.warn so authors who typo
        /// the mode see a signal instead of silent default behavior. The Mode attribute
        /// default is "\"alpha\"" (JSON-string-encoded by the C# template) and parses
        /// through stripQuotes to the bare token "alpha".
        async evalImageMask(node) {
            const imgLink = this.findLinkTo(node.Id, 'Image');
            if (!imgLink) {
                console.warn('[Visualist] Image.Mask: required Image input not connected');
                return null;
            }
            const maskLink = this.findLinkTo(node.Id, 'Mask');
            if (!maskLink) {
                console.warn('[Visualist] Image.Mask: required Mask input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(imgLink.FromNodeId, imgLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;
            const maskRes  = await this.evalNodeOutput(maskLink.FromNodeId, maskLink.FromSocketId);
            if (!maskRes || !maskRes.image) {
                console.warn('[Visualist] Image.Mask: Mask upstream produced no image');
                return null;
            }

            // L40 — explicit Mode attribute drives alpha-vs-luminance.
            let mode = stripQuotes(attr(node, 'Mode', '"alpha"')) || 'alpha';
            if (mode !== 'alpha' && mode !== 'luminance') {
                console.warn(
                    `[Visualist] Image.Mask: unknown Mode '${mode}' — expected ` +
                    `'alpha' or 'luminance'. Falling back to 'alpha'.`);
                mode = 'alpha';
            }
            const w  = upstream.width  || upstream.image.width;
            const ih = upstream.height || upstream.image.height;
            // Crop-only-on-export — the mask may differ in extent from the image now
            // that Image.Transform grows to hold off-widget content. Both share the
            // widget-centre anchor, so centre-align the mask onto the image canvas.
            // Image pixels outside the mask's rect get no destination-in coverage and
            // clear — the intended "mask defines visibility" (an authored crop).
            const mw  = maskRes.width  || maskRes.image.width;
            const mh  = maskRes.height || maskRes.image.height;
            const mdx = (w - mw) / 2, mdy = (ih - mh) / 2;

            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.drawImage(upstream.image, 0, 0, w, ih);

            if (mode === 'luminance') {
                // L40 luminance mode — convert mask to alpha-from-luminance via a
                // scratch canvas + grayscale filter, then composite as alpha.
                // Sprint 71 — pooled scratch (the canvas is never returned to a
                // downstream consumer, only drawn-then-released this frame).
                const scratch = canvasPool.acquire(mw, mh);
                const sctx = scratch.getContext('2d');
                sctx.filter = 'grayscale(1)';
                sctx.drawImage(maskRes.image, 0, 0, mw, mh);
                octx.globalCompositeOperation = 'destination-in';
                octx.drawImage(scratch, mdx, mdy);
                canvasPool.release(scratch);
            } else {
                // L40 alpha mode (default) — straight destination-in composite of
                // the raw mask image's alpha channel.
                octx.globalCompositeOperation = 'destination-in';
                octx.drawImage(maskRes.image, mdx, mdy, mw, mh);
            }
            return { image: off, width: w, height: ih };
        }

        // ── Sweep 22 — procedural mask shape generators ─────────────────────
        // Each generator produces an Image at the layer's resolution. White +
        // alpha (rgba(255,255,255,a)) inside the shape, transparent outside —
        // exactly what Image.Mask's destination-in compositor expects.
        //
        // All scalar params are read via attrAnimated() so keyframe animation
        // (sweep 21) Just Works. Boolean Inverted is read via plain attr() and
        // string-compared to "true" because attrAnimated only handles numerics.
        //
        // Order of operations for hard shapes with both Feather and Inverted:
        // shape → feather (blur) → invert. Inverting last keeps the feathered
        // edge as a soft transition rather than a hard line.

        _maskCanvas() {
            const w = (layer && layer.resolution) ? layer.resolution.width  : logicalW;
            const h = (layer && layer.resolution) ? layer.resolution.height : logicalH;
            // Sprint 7 — mask-canvas allocation routes through acquireEscape
            // because every caller returns this canvas as `{ image: off, … }`.
            // The Evaluator's renderWidgetTrigger finally releases the batch
            // once Display has copied pixels onto the visible context.
            const off = this.acquireEscape(w, h);
            return { canvas: off, ctx: off.getContext('2d'), w, h };
        }

        _maskApplyFeather(ctx, w, h, featherNorm) {
            if (!(featherNorm > 0)) return;
            // Feather is normalised to the SHORTER axis so a square mask blurs
            // evenly across landscape vs portrait layers. Cap at half the short
            // axis so the blur never wipes the entire shape.
            const shortAxis = Math.min(w, h);
            const px = Math.max(0, Math.min(featherNorm, 0.5)) * shortAxis;
            if (px <= 0.5) return;
            // Snapshot, then re-draw blurred. ctx.filter resets after each draw
            // so we apply it fresh here. Sprint 83 — pooled scratch (the snap is
            // drawn back into ctx then dropped, never returned to a consumer).
            const snap = canvasPool.acquire(w, h);
            snap.getContext('2d').drawImage(ctx.canvas, 0, 0);
            ctx.clearRect(0, 0, w, h);
            ctx.filter = `blur(${px}px)`;
            ctx.drawImage(snap, 0, 0);
            ctx.filter = 'none';
            canvasPool.release(snap);
        }

        _maskApplyInvert(ctx, w, h, inverted) {
            if (!inverted) return;
            // destination-out with opaque white over the whole canvas flips alpha:
            // opaque pixels become transparent, transparent pixels stay transparent.
            // Then re-fill the empty pixels with opaque white via destination-over.
            ctx.globalCompositeOperation = 'destination-out';
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.fillRect(0, 0, w, h);
            ctx.globalCompositeOperation = 'destination-over';
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.fillRect(0, 0, w, h);
            ctx.globalCompositeOperation = 'source-over';
        }

        /// Reads a bool-valued node attribute. true / 1 / yes — any case, surrounding whitespace
        /// ignored — are TRUE; everything else, an absent attribute included, is FALSE.
        ///
        /// ★ SURROUNDING DOUBLE QUOTES ARE STRIPPED FIRST, and that is DEFENCE IN DEPTH, not a
        /// shape this file wants to see. The shape a bool is supposed to have is bare
        /// true / false: NodeParamKind.Bool commits unquoted, and the catalog-wide rule that no
        /// bool-valued attribute may ship a <Key>__KnownValues companion exists to keep it that
        /// way (the Enum control kind outranks everything in ResolveParamKind, and its CommitText
        /// JSON-quotes the value). That rule is enforced template-side by
        /// WidgetFamilyV10Tests.No_BoolValued_Attribute_In_The_Catalog_Ships_A_KnownValues_Companion.
        ///
        /// But the template is not the only thing that can put a quoted "true" in front of this
        /// reader. The guard scans WidgetNodeRegistry.Templates, while the Inspector resolves the
        /// control kind off node.Attributes, and LayerGraphMigrator.BackfillFromTemplate is
        /// ADDITIVE ONLY — it never prunes a key the template has since dropped. So a node
        /// serialised while a companion existed keeps its quoted value forever, invisibly to the
        /// template-side guard, and a hand-edited .phxlayer can carry the same shape. Stripping
        /// here makes both read correctly instead of silently FALSE with the Inspector showing the
        /// right word — the failure class this whole rework exists to remove.
        ///
        /// Stripping cannot change what any current caller means: every _readBool site reads
        /// Text.Render's Wrap or a Mask.* Inverted / Closed, all of which ship BARE true/false
        /// defaults, so there are no quotes to remove in the intended shape. (Image.Tile's Wrap is
        /// a quoted STRING enum and does not come through here.) The trim runs before the strip so
        /// a padded "true" still presents its quotes as first and last character, and again after
        /// so whitespace inside the quotes is ignored too.
        _readBool(node, key) {
            const raw = stripQuotes((attr(node, key, 'false') || 'false').toString().trim())
                .toLowerCase().trim();
            return raw === 'true' || raw === '1' || raw === 'yes';
        }

        evalMaskRectangle(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const x  = (parseFloat(attrAnimated(node, 'X',            '0')) || 0) * w;
            const y  = (parseFloat(attrAnimated(node, 'Y',            '0')) || 0) * h;
            const ww = (parseFloat(attrAnimated(node, 'Width',        '1')) || 0) * w;
            const hh = (parseFloat(attrAnimated(node, 'Height',       '1')) || 0) * h;
            const r  = (parseFloat(attrAnimated(node, 'CornerRadius', '0')) || 0) * Math.min(w, h);
            const fe =  parseFloat(attrAnimated(node, 'Feather',      '0')) || 0;
            const inv = this._readBool(node, 'Inverted');
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.beginPath();
            if (r > 0 && typeof ctx.roundRect === 'function') {
                ctx.roundRect(x, y, ww, hh, Math.min(r, ww / 2, hh / 2));
            } else {
                ctx.rect(x, y, ww, hh);
            }
            ctx.fill();
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        evalMaskCircle(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const cx  = (parseFloat(attrAnimated(node, 'CX',     '0.5'))  || 0) * w;
            const cy  = (parseFloat(attrAnimated(node, 'CY',     '0.5'))  || 0) * h;
            // Radius normalised against shorter axis so the circle stays circular
            // regardless of layer aspect.
            const r   = (parseFloat(attrAnimated(node, 'Radius', '0.25')) || 0) * Math.min(w, h);
            const fe  =  parseFloat(attrAnimated(node, 'Feather', '0'))   || 0;
            const inv = this._readBool(node, 'Inverted');
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.beginPath();
            ctx.arc(cx, cy, Math.max(0, r), 0, Math.PI * 2);
            ctx.fill();
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        evalMaskEllipse(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const cx   = (parseFloat(attrAnimated(node, 'CX',       '0.5')) || 0) * w;
            const cy   = (parseFloat(attrAnimated(node, 'CY',       '0.5')) || 0) * h;
            const rx   = (parseFloat(attrAnimated(node, 'RadiusX',  '0.3')) || 0) * w;
            const ry   = (parseFloat(attrAnimated(node, 'RadiusY',  '0.2')) || 0) * h;
            const rot  = (parseFloat(attrAnimated(node, 'Rotation', '0'))   || 0) * Math.PI / 180;
            const fe   =  parseFloat(attrAnimated(node, 'Feather',  '0'))   || 0;
            const inv  = this._readBool(node, 'Inverted');
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.beginPath();
            ctx.ellipse(cx, cy, Math.max(0, rx), Math.max(0, ry), rot, 0, Math.PI * 2);
            ctx.fill();
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        evalMaskLinearGradient(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const fx = (parseFloat(attrAnimated(node, 'FromX',     '0'))   || 0) * w;
            const fy = (parseFloat(attrAnimated(node, 'FromY',     '0.5')) || 0) * h;
            const tx = (parseFloat(attrAnimated(node, 'ToX',       '1'))   || 0) * w;
            const ty = (parseFloat(attrAnimated(node, 'ToY',       '0.5')) || 0) * h;
            const fa = Math.max(0, Math.min(1, parseFloat(attrAnimated(node, 'FromAlpha', '1')) || 0));
            const ta = Math.max(0, Math.min(1, parseFloat(attrAnimated(node, 'ToAlpha',   '0')) || 0));
            const grad = ctx.createLinearGradient(fx, fy, tx, ty);
            grad.addColorStop(0, `rgba(255,255,255,${fa})`);
            grad.addColorStop(1, `rgba(255,255,255,${ta})`);
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, w, h);
            return { image: off, width: w, height: h };
        }

        evalMaskRadialGradient(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const cx  = (parseFloat(attrAnimated(node, 'CX',          '0.5')) || 0) * w;
            const cy  = (parseFloat(attrAnimated(node, 'CY',          '0.5')) || 0) * h;
            const ri  = (parseFloat(attrAnimated(node, 'InnerRadius', '0'))   || 0) * Math.min(w, h);
            const ro  = (parseFloat(attrAnimated(node, 'OuterRadius', '0.5')) || 0) * Math.min(w, h);
            const ia  = Math.max(0, Math.min(1, parseFloat(attrAnimated(node, 'InnerAlpha', '1')) || 0));
            const oa  = Math.max(0, Math.min(1, parseFloat(attrAnimated(node, 'OuterAlpha', '0')) || 0));
            // createRadialGradient requires r0 < r1 and both >= 0. Snap if user
            // animated through an invalid configuration.
            const r0 = Math.max(0, Math.min(ri, ro));
            const r1 = Math.max(r0 + 0.001, Math.max(ri, ro));
            const grad = ctx.createRadialGradient(cx, cy, r0, cx, cy, r1);
            grad.addColorStop(0, `rgba(255,255,255,${ia})`);
            grad.addColorStop(1, `rgba(255,255,255,${oa})`);
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, w, h);
            return { image: off, width: w, height: h };
        }

        evalMaskVignette(node) {
            // Vignette is a one-knob preset: centred radial gradient. Strength
            // controls how dark the corners get — 0 = invisible, 1 = corners go
            // fully transparent (so Image.Mask erases them).
            const strength = Math.max(0, Math.min(1, parseFloat(attrAnimated(node, 'Strength', '0.5')) || 0));
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const cx = w * 0.5, cy = h * 0.5;
            const ri = Math.min(w, h) * 0.30;
            const ro = Math.min(w, h) * 0.85;
            const grad = ctx.createRadialGradient(cx, cy, ri, cx, cy, ro);
            grad.addColorStop(0, `rgba(255,255,255,1)`);
            grad.addColorStop(1, `rgba(255,255,255,${1 - strength})`);
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, w, h);
            return { image: off, width: w, height: h };
        }

        // ── Sweep 23 — vertex-list (Polygon/Bezier) + Star generators ───────
        // The Vertices attribute is a JSON list of { x, y, cp1x?, cp1y?, cp2x?,
        // cp2y? } objects (normalised 0..1). Per-vertex animation: the keyframe
        // sampler's parameterPath grammar gains `vertex[N].<axis>`; resolved
        // by vertexAnimated() below. The C# ShapeData class is the editor-side
        // mirror and the producer of these path strings.
        // Both shapes share the closed-or-open + feather + invert pipeline with
        // sweep 22's hard shapes via the `_maskApplyFeather` / `_maskApplyInvert`
        // helpers.

        _parseVertices(node) {
            // Static fallback if attr is missing/malformed; the kernel still
            // renders an empty mask which is a clear "blank shape" signal.
            const raw = attr(node, 'Vertices', '[]');
            if (typeof raw !== 'string') return [];
            try {
                const parsed = JSON.parse(raw);
                if (!Array.isArray(parsed)) return [];
                // Cap at 256 vertices (mirror of ShapeData.MaxVertices).
                return parsed.slice(0, 256).map(v => ({
                    x:    typeof v.x    === 'number' ? v.x    : 0,
                    y:    typeof v.y    === 'number' ? v.y    : 0,
                    cp1x: typeof v.cp1x === 'number' ? v.cp1x : null,
                    cp1y: typeof v.cp1y === 'number' ? v.cp1y : null,
                    cp2x: typeof v.cp2x === 'number' ? v.cp2x : null,
                    cp2y: typeof v.cp2y === 'number' ? v.cp2y : null,
                }));
            } catch { return []; }
        }

        // Resolve a per-vertex coord, honouring keyframes at
        // `vertex[N].<axis>` paths. Falls back to the static value sourced
        // from the parsed Vertices JSON.
        _vertexCoord(node, vertexIndex, axis, staticValue) {
            const fallback = (staticValue == null || !Number.isFinite(staticValue)) ? '0' : String(staticValue);
            const v = parseFloat(attrAnimated(node, `vertex[${vertexIndex}].${axis}`, fallback));
            return Number.isFinite(v) ? v : 0;
        }

        evalMaskPolygon(node) {
            const verts = this._parseVertices(node);
            const closed = this._readBool(node, 'Closed') || (attr(node, 'Closed', 'true') === 'true');
            const fe     = parseFloat(attrAnimated(node, 'Feather', '0')) || 0;
            const inv    = this._readBool(node, 'Inverted');
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            if (verts.length >= 2) {
                ctx.fillStyle = 'rgba(255,255,255,1)';
                ctx.beginPath();
                for (let i = 0; i < verts.length; i++) {
                    const x = this._vertexCoord(node, i, 'x', verts[i].x) * w;
                    const y = this._vertexCoord(node, i, 'y', verts[i].y) * h;
                    if (i === 0) ctx.moveTo(x, y);
                    else         ctx.lineTo(x, y);
                }
                if (closed) ctx.closePath();
                ctx.fill();
            }
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        evalMaskBezier(node) {
            const verts  = this._parseVertices(node);
            const closed = this._readBool(node, 'Closed') || (attr(node, 'Closed', 'true') === 'true');
            const fe     = parseFloat(attrAnimated(node, 'Feather', '0')) || 0;
            const inv    = this._readBool(node, 'Inverted');
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            if (verts.length >= 2) {
                ctx.fillStyle = 'rgba(255,255,255,1)';
                ctx.beginPath();
                // Resolve every vertex's animated coords once up-front so we can
                // refer to the previous vertex's outgoing handle (cp2) when
                // drawing the curve into the current vertex's incoming (cp1).
                const r = verts.map((v, i) => ({
                    x:    this._vertexCoord(node, i, 'x',    v.x)    * w,
                    y:    this._vertexCoord(node, i, 'y',    v.y)    * h,
                    cp1x: (v.cp1x != null) ? this._vertexCoord(node, i, 'cp1x', v.cp1x) * w : null,
                    cp1y: (v.cp1y != null) ? this._vertexCoord(node, i, 'cp1y', v.cp1y) * h : null,
                    cp2x: (v.cp2x != null) ? this._vertexCoord(node, i, 'cp2x', v.cp2x) * w : null,
                    cp2y: (v.cp2y != null) ? this._vertexCoord(node, i, 'cp2y', v.cp2y) * h : null,
                }));
                ctx.moveTo(r[0].x, r[0].y);
                for (let i = 1; i < r.length; i++) {
                    const a = r[i - 1], b = r[i];
                    // bezierCurveTo(cp1x_outgoing_from_a, cp1y_outgoing_from_a,
                    //               cp2x_incoming_to_b,   cp2y_incoming_to_b,
                    //               b.x, b.y).
                    // Our vertex schema names "cp2" as the OUTGOING handle from
                    // a vertex and "cp1" as the INCOMING handle. So the outgoing
                    // bezier handle is a.cp2, incoming to b is b.cp1.
                    const c1x = (a.cp2x != null) ? a.cp2x : a.x;
                    const c1y = (a.cp2y != null) ? a.cp2y : a.y;
                    const c2x = (b.cp1x != null) ? b.cp1x : b.x;
                    const c2y = (b.cp1y != null) ? b.cp1y : b.y;
                    ctx.bezierCurveTo(c1x, c1y, c2x, c2y, b.x, b.y);
                }
                if (closed && r.length >= 2) {
                    // Closing segment: from last vertex back to first using
                    // last.cp2 → first.cp1.
                    const a = r[r.length - 1], b = r[0];
                    const c1x = (a.cp2x != null) ? a.cp2x : a.x;
                    const c1y = (a.cp2y != null) ? a.cp2y : a.y;
                    const c2x = (b.cp1x != null) ? b.cp1x : b.x;
                    const c2y = (b.cp1y != null) ? b.cp1y : b.y;
                    ctx.bezierCurveTo(c1x, c1y, c2x, c2y, b.x, b.y);
                    ctx.closePath();
                }
                ctx.fill();
            }
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        evalMaskStar(node) {
            const { canvas: off, ctx, w, h } = this._maskCanvas();
            const ccx   = (parseFloat(attrAnimated(node, 'CX',          '0.5'))  || 0) * w;
            const ccy   = (parseFloat(attrAnimated(node, 'CY',          '0.5'))  || 0) * h;
            const ro    = (parseFloat(attrAnimated(node, 'OuterRadius', '0.4'))  || 0) * Math.min(w, h);
            const ri    = (parseFloat(attrAnimated(node, 'InnerRadius', '0.18')) || 0) * Math.min(w, h);
            // Points clamps to 3..16; non-integer values floor.
            let pts = parseInt(attrAnimated(node, 'Points', '5'), 10);
            if (!Number.isFinite(pts) || pts < 3) pts = 3;
            if (pts > 16) pts = 16;
            const rot = ((parseFloat(attrAnimated(node, 'Rotation', '0')) || 0) - 90) * Math.PI / 180;
            const fe  =  parseFloat(attrAnimated(node, 'Feather',  '0')) || 0;
            const inv = this._readBool(node, 'Inverted');
            ctx.fillStyle = 'rgba(255,255,255,1)';
            ctx.beginPath();
            const total = pts * 2;
            for (let i = 0; i < total; i++) {
                const a = rot + (i * Math.PI) / pts;
                const r = (i % 2 === 0) ? ro : ri;
                const x = ccx + Math.cos(a) * r;
                const y = ccy + Math.sin(a) * r;
                if (i === 0) ctx.moveTo(x, y);
                else         ctx.lineTo(x, y);
            }
            ctx.closePath();
            ctx.fill();
            this._maskApplyFeather(ctx, w, h, fe);
            this._maskApplyInvert(ctx, w, h, inv);
            return { image: off, width: w, height: h };
        }

        // ── V10 — Image.Solid: the colour plate, and the goal BAR's primitive ───
        //
        /// Emits a Colour-filled rounded rectangle as an Image. The rectangle is expressed as
        /// 0..1 fractions and every geometry pin is WIRABLE, which is the whole reason the node
        /// exists: it is the only fill geometry a live channel value can drive, so
        /// `Goal.Progress.Progress → Width` IS the bar.
        ///
        /// ── GEOMETRY SPACE, a deliberate divergence from the Mask.* family above ──
        /// Mask fractions are of the LAYER; these are of the WIDGET FRAME. The frame is the
        /// space Display draws 1:1 and the space Text.Render already rasterises into, so
        /// Width 0.6 means "60% of MY widget" — the only reading under which a bar and its own
        /// caption compose. Fractions of the layer would make the same bar change length when
        /// the author moved the widget.
        ///
        /// ── THE RENDER CONTRACT (dep d), and why the extent is not simply the frame ──
        /// The rule is: compose in CONTENT-EXTENT space centred on the widget centre, and crop
        /// ONLY at Display. So this kernel does not clip the author's rectangle to the frame.
        /// The emitted extent is the frame's half-size about the FRAME CENTRE, grown to contain
        /// an overhanging rect:
        ///
        ///     halfX = max(0.5, |x0 - 0.5|, |x1 - 0.5|)   (and likewise for Y)
        ///     extent = 2 * half * frame
        ///
        /// Consequences, both intended:
        ///   • a rect inside 0..1 (every normal bar) yields EXACTLY the frame, so this is
        ///     byte-identical to the naive frame-sized implementation for the common case and
        ///     composes with a frame-sized Text.Render at a 1:1 union extent.
        ///   • a rect that overhangs (Width 1.4 for a bar that bleeds off the widget, a negative
        ///     X for an entry slide) grows the canvas symmetrically instead of losing the
        ///     overhang, and Display performs the single crop. Growing symmetrically is what
        ///     keeps the widget centre mapped to the canvas centre, which is the anchor every
        ///     downstream consumer centre-aligns on (see Image.Blend / Image.Combine).
        /// The extent never SHRINKS below the frame: a narrow bar keeps a frame-sized canvas so
        /// its position inside the frame is carried by the pixels rather than by an extent a
        /// downstream union would then have to reason about.
        ///
        /// Colour is wired-socket-wins with attrAnimatedColor as the fallback, so a keyframed
        /// colour animates; the four geometry attributes read through attrAnimated for the same
        /// reason. That keyframe path is also how an author previews a bar while building, since
        /// a channel-fed Scalar honestly reads 0 on a canvas with no channel behind it.
        async evalImageSolid(node) {
            // Frame in LOGICAL widget pixels. The layer resolution is the fallback for an
            // Evaluator built without a frame (tooling / legacy construction) — the same
            // fallback _maskCanvas uses, so a standalone evaluation still produces something
            // sensible rather than a 1×1.
            const frame = this.frame;
            const fw = Math.max(1, Math.round(frame ? frame.width
                : ((layer && layer.resolution) ? layer.resolution.width : logicalW)));
            const fh = Math.max(1, Math.round(frame ? frame.height
                : ((layer && layer.resolution) ? layer.resolution.height : logicalH)));

            const colorLink = this.findLinkTo(node.Id, 'Color');
            let color = attrAnimatedColor(node, 'Color', '"#ffffff"');
            if (colorLink) {
                const v = await this.evalNodeOutput(colorLink.FromNodeId, colorLink.FromSocketId);
                if (v) color = colorToCss(v);
            }

            // NaN guard on every geometry pin. _evalAnimScalarSocket forwards a wired number
            // as-is and NaN is typeof 'number', so a NaN arriving from a Math chain would reach
            // fillRect and silently paint nothing while the extent math produced NaN dimensions
            // (canvasPool then allocates a 0×0 canvas). Refuse it at the door.
            const fin = (n, d) => Number.isFinite(n) ? n : d;
            const x = fin(await this._evalAnimScalarSocket(node, 'X',      0), 0);
            const y = fin(await this._evalAnimScalarSocket(node, 'Y',      0), 0);
            const w = fin(await this._evalAnimScalarSocket(node, 'Width',  1), 1);
            const h = fin(await this._evalAnimScalarSocket(node, 'Height', 1), 1);

            // Ordered span, so a NEGATIVE width (legal, and what a mirrored/right-anchored bar
            // driven off one Math.Sub produces) describes the same rectangle drawn leftwards
            // rather than an inverted extent.
            const x0 = Math.min(x, x + w), x1 = Math.max(x, x + w);
            const y0 = Math.min(y, y + h), y1 = Math.max(y, y + h);

            const halfX = Math.max(0.5, Math.abs(x0 - 0.5), Math.abs(x1 - 0.5));
            const halfY = Math.max(0.5, Math.abs(y0 - 0.5), Math.abs(y1 - 0.5));
            // Cap the growth so an absurd wired value (a bar driven by an unclamped counter)
            // cannot ask for a gigapixel canvas. 8× the frame is far past anything Display can
            // show and keeps the pool's own 16 MP ceiling meaningful.
            const cw = Math.max(1, Math.round(Math.min(8, 2 * halfX) * fw));
            const ch = Math.max(1, Math.round(Math.min(8, 2 * halfY) * fh));

            const off  = this.acquireEscape(cw, ch);
            const octx = off.getContext('2d');
            // Frame origin inside the (possibly grown) canvas — the two centres coincide.
            const ox = (cw - fw) / 2;
            const oy = (ch - fh) / 2;
            const rx = ox + x0 * fw;
            const ry = oy + y0 * fh;
            const rw = (x1 - x0) * fw;
            const rh = (y1 - y0) * fh;
            // A zero-extent rect is a legitimate state — it is what progress 0 looks like — so
            // the canvas is still returned, transparent. Returning null instead would make an
            // Image.Blend drop the whole branch and an empty bar would take its own frame with it.
            if (rw > 0 && rh > 0) {
                // CornerRadius is a fraction of the shorter FRAME axis (Mask.Rectangle's
                // convention) and is attribute-only: a pill bar sets it once and never drives it
                // from data. Clamped to half the rect's shorter side so a short bar stays a
                // capsule instead of throwing on an over-large radius.
                const r = Math.max(0, (parseFloat(attrAnimated(node, 'CornerRadius', '0')) || 0))
                        * Math.min(fw, fh);
                octx.fillStyle = color;
                octx.beginPath();
                if (r > 0 && typeof octx.roundRect === 'function') {
                    octx.roundRect(rx, ry, rw, rh, Math.min(r, rw / 2, rh / 2));
                } else {
                    octx.rect(rx, ry, rw, rh);
                }
                octx.fill();
            }
            return { image: off, width: cw, height: ch };
        }

        /// Image.Blend — blends Top (B) over Bottom (A) using a CSS blend mode.
        /// The Mode attribute must be one of the standard CSS / Canvas2D blend
        /// modes; unknown values fall back to source-over with a warn (matching
        /// the C# evaluator's _validBlendModes check).
        async evalImageBlend(node) {
            const aLink = this.findLinkTo(node.Id, 'A');
            const bLink = this.findLinkTo(node.Id, 'B');
            const aRaw = aLink ? await this.evalNodeOutput(aLink.FromNodeId, aLink.FromSocketId) : null;
            const bRaw = bLink ? await this.evalNodeOutput(bLink.FromNodeId, bLink.FromSocketId) : null;
            // A blend composites B (top) over A (bottom). An absent/empty layer
            // contributes nothing — return the OTHER layer rather than discarding a
            // valid image because its partner is empty (the bug: an unfilled
            // Text.Render caption over a loaded image collapsed the whole blend to
            // "load failed"). Coerce a data-only side so e.g. a Color over an image
            // still composites. Only fail when NEITHER side yields an image.
            const a = (aRaw && aRaw.image) ? aRaw : this.coerceToImage(aRaw);
            const b = (bRaw && bRaw.image) ? bRaw : this.coerceToImage(bRaw);
            if ((!a || !a.image) && (!b || !b.image)) return null;
            if (!a || !a.image) return b;
            if (!b || !b.image) return a;

            let mode = stripQuotes(attr(node, 'Mode', '"normal"')) || 'normal';
            if (mode === 'normal') mode = 'source-over';
            if (!VALID_BLEND_MODES.has(mode)) {
                console.warn(`[Visualist] Image.Blend: unknown Mode '${mode}' — falling back to source-over`);
                mode = 'source-over';
            }

            const opacity = await this._evalScalarSocket(
                node, 'Opacity', parseFloat(attr(node, 'Opacity', '1')) || 1);

            // Crop-only-on-export — A and B may now have DIFFERENT extents (a grown
            // Image.Transform vs a frame-sized Text.Render). Both share the widget-
            // centre anchor, so compose into the UNION extent (max per axis) and
            // CENTRE-ALIGN both — instead of sizing to A and top-left-stretching B,
            // which clipped/distorted the larger input. Off-widget content survives to
            // Display, which performs the single widget crop.
            const aw = a.width  || a.image.width;
            const ah = a.height || a.image.height;
            const bw = b.width  || b.image.width;
            const bh = b.height || b.image.height;
            const w  = Math.max(aw, bw);
            const ih = Math.max(ah, bh);
            // Bug #1 (text blurry) — preserve the denser of the two inputs' pixel
            // density through the composite so a supersampled Text.Render (often the
            // B layer over an image A) stays sharp instead of being flattened to
            // logical here. dens = max(physical/logical) across A and B; back the
            // offscreen at logical×dens, pre-scale the ctx so the composite math stays
            // logical, and draw each FULL physical source into its centred logical rect
            // (9-arg) so density carries to Display, which downsamples it sharp.
            const dens = Math.max(1, Math.min(4, Math.round(Math.max(
                a.image.width / Math.max(1, aw),
                b.image.width / Math.max(1, bw)))));
            const off = this.acquireEscape(Math.round(w * dens), Math.round(ih * dens));
            const octx = off.getContext('2d');
            octx.scale(dens, dens);
            octx.drawImage(a.image, 0, 0, a.image.width, a.image.height, (w - aw) / 2, (ih - ah) / 2, aw, ah);
            octx.globalCompositeOperation = mode;
            octx.globalAlpha = Math.max(0, Math.min(1, opacity));
            octx.drawImage(b.image, 0, 0, b.image.width, b.image.height, (w - bw) / 2, (ih - bh) / 2, bw, bh);
            // Reset for any downstream draws that share the canvas (defensive).
            octx.globalCompositeOperation = 'source-over';
            octx.globalAlpha = 1;
            return { image: off, width: w, height: ih };
        }

        /// Image.Combine — unified blend + key node. Mode handles the standard
        /// CSS blend operations (multiply/screen/overlay/etc) AND three key
        /// modes that derive top-input alpha from luminance:
        ///   • 'alpha-key'      — keep B's existing alpha (B over A, premultiplied)
        ///   • 'luminance-key'  — derive alpha from B's luminance (black → transparent)
        ///   • 'inv-luminance'  — inverse: white → transparent, black → opaque
        /// Mirrors C# NodeEvaluator.EvalImageCombine.
        async evalImageCombine(node) {
            const aLink = this.findLinkTo(node.Id, 'A');
            const bLink = this.findLinkTo(node.Id, 'B');
            const aRaw = aLink ? await this.evalNodeOutput(aLink.FromNodeId, aLink.FromSocketId) : null;
            const bRaw = bLink ? await this.evalNodeOutput(bLink.FromNodeId, bLink.FromSocketId) : null;
            // Same empty-tolerance as Image.Blend: a missing/empty side contributes
            // nothing, so return the other rather than dropping a valid image.
            const a = (aRaw && aRaw.image) ? aRaw : this.coerceToImage(aRaw);
            const b = (bRaw && bRaw.image) ? bRaw : this.coerceToImage(bRaw);
            if ((!a || !a.image) && (!b || !b.image)) return null;
            if (!a || !a.image) return b;
            if (!b || !b.image) return a;

            let mode = stripQuotes(attr(node, 'Mode', '"normal"')) || 'normal';
            const isKey = mode === 'alpha-key' || mode === 'luminance-key' || mode === 'inv-luminance';
            if (!isKey) {
                if (mode === 'normal') mode = 'source-over';
                if (!VALID_BLEND_MODES.has(mode)) {
                    console.warn(`[Visualist] Image.Combine: unknown Mode '${mode}' — falling back to source-over`);
                    mode = 'source-over';
                }
            }

            const opacity = await this._evalScalarSocket(
                node, 'Opacity', parseFloat(attr(node, 'Opacity', '1')) || 1);

            // Crop-only-on-export — union extent + centre-align both inputs (see the
            // matching block in Image.Blend for the rationale).
            const aw = a.width  || a.image.width;
            const ah = a.height || a.image.height;
            const bw = b.width  || b.image.width;
            const bh = b.height || b.image.height;
            const w  = Math.max(aw, bw);
            const ih = Math.max(ah, bh);
            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');
            octx.drawImage(a.image, (w - aw) / 2, (ih - ah) / 2, aw, ah);

            if (isKey) {
                // Build a keyed copy of B whose alpha channel reflects the
                // chosen luminance/alpha rule, then composite with source-over.
                // Sprint 83 — applyKeyMode now acquires from canvasPool and the
                // caller is responsible for releasing once the keyed copy has
                // been composited into `off`. After this drawImage the pixels
                // are in `off`'s bitmap and `keyed` itself is no longer read.
                const keyed = applyKeyMode(b.image, bw, bh, mode);
                octx.globalAlpha = Math.max(0, Math.min(1, opacity));
                octx.drawImage(keyed, (w - bw) / 2, (ih - bh) / 2, bw, bh);
                canvasPool.release(keyed);
            } else {
                octx.globalCompositeOperation = mode;
                octx.globalAlpha = Math.max(0, Math.min(1, opacity));
                octx.drawImage(b.image, (w - bw) / 2, (ih - bh) / 2, bw, bh);
            }
            // Reset for any downstream draws sharing this canvas.
            octx.globalCompositeOperation = 'source-over';
            octx.globalAlpha = 1;
            return { image: off, width: w, height: ih };
        }

        /// Image.Tile — fills the widget rect with the source image.
        ///
        /// Repeat counts come from the Repeat Vector2 socket (X = cols, Y = rows)
        /// or from a comma-separated attribute fallback. When neither is set,
        /// defaults to 1×1 (single tile, no scaling).
        ///
        /// L41 — explicit Wrap attribute drives the per-edge tiling behavior:
        ///   • wrap = "repeat" (default) — straight tiled repeat. Implemented via
        ///                                  CanvasRenderingContext2D.createPattern
        ///                                  with the 'repeat' repetition string.
        ///   • wrap = "mirror"           — alternating-flip tiling. Even tiles
        ///                                  draw normal, odd tiles flip on the
        ///                                  axis they're crossing. Implemented by
        ///                                  building a 2x2 pre-mirrored super-tile
        ///                                  and then 'repeat'-patterning that —
        ///                                  cheaper than nested transformed draws
        ///                                  for large tile counts.
        ///   • wrap = "clamp"            — single tile in the top-left, the rest
        ///                                  of the rect filled with the edge
        ///                                  pixels stretched to the bounds.
        ///                                  Implemented via 'no-repeat' pattern +
        ///                                  edge-stretch drawImage calls.
        /// Unknown values fall back to 'repeat' with a console.warn so authors who
        /// typo the wrap mode see a signal instead of silent defaulting.
        async evalImageTile(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) {
                console.warn('[Visualist] Image.Tile: required In input not connected');
                return null;
            }
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const repeatLink = this.findLinkTo(node.Id, 'Repeat');
            let cols = 1, rows = 1;
            if (repeatLink) {
                const v = await this.evalNodeOutput(repeatLink.FromNodeId, repeatLink.FromSocketId);
                if (v && typeof v.x === 'number') cols = Math.max(1, Math.round(v.x));
                if (v && typeof v.y === 'number') rows = Math.max(1, Math.round(v.y));
            } else {
                cols = Math.max(1, Math.round(parseFloat(attr(node, 'RepeatX', '1')) || 1));
                rows = Math.max(1, Math.round(parseFloat(attr(node, 'RepeatY', '1')) || 1));
            }

            // L41 — wrap mode. Default 'repeat' preserves the prior behavior so
            // existing layers render identically without setting Wrap.
            let wrap = stripQuotes(attr(node, 'Wrap', '"repeat"')) || 'repeat';
            if (wrap !== 'repeat' && wrap !== 'mirror' && wrap !== 'clamp') {
                console.warn(
                    `[Visualist] Image.Tile: unknown Wrap '${wrap}' — expected ` +
                    `'repeat', 'mirror', or 'clamp'. Falling back to 'repeat'.`);
                wrap = 'repeat';
            }

            const sw = upstream.width  || upstream.image.width;
            const sh = upstream.height || upstream.image.height;
            const w  = sw * cols;
            const ih = sh * rows;

            const off = this.acquireEscape(w, ih);
            const octx = off.getContext('2d');

            if (wrap === 'mirror') {
                // L41 mirror — pre-render a 2x2 super-tile with horizontally and
                // vertically flipped copies of the source, then 'repeat'-pattern that.
                // The super-tile is twice the source dims on each axis. Sprint 83 —
                // `sup` is consumed by the createPattern → fillRect (or fallback
                // drawImage) below; once those land pixels in `off`, sup itself
                // never escapes, so it's pooled.
                const sup = canvasPool.acquire(sw * 2, sh * 2);
                const supctx = sup.getContext('2d');
                // (0, 0) — original.
                supctx.drawImage(upstream.image, 0, 0, sw, sh);
                // (sw, 0) — horizontal flip.
                supctx.save();
                supctx.translate(sw * 2, 0);
                supctx.scale(-1, 1);
                supctx.drawImage(upstream.image, 0, 0, sw, sh);
                supctx.restore();
                // (0, sh) — vertical flip.
                supctx.save();
                supctx.translate(0, sh * 2);
                supctx.scale(1, -1);
                supctx.drawImage(upstream.image, 0, 0, sw, sh);
                supctx.restore();
                // (sw, sh) — both flips.
                supctx.save();
                supctx.translate(sw * 2, sh * 2);
                supctx.scale(-1, -1);
                supctx.drawImage(upstream.image, 0, 0, sw, sh);
                supctx.restore();
                const pattern = octx.createPattern(sup, 'repeat');
                if (pattern) {
                    octx.fillStyle = pattern;
                    octx.fillRect(0, 0, w, ih);
                } else {
                    octx.drawImage(sup, 0, 0);
                }
                canvasPool.release(sup);
            } else if (wrap === 'clamp') {
                // L41 clamp — single tile at the origin, remainder stretched from
                // the edge pixels. Use 'no-repeat' pattern semantics and stamp
                // the source once; if the requested rect is larger than a single
                // tile, stretch the source edges to fill the gap.
                const pattern = octx.createPattern(upstream.image, 'no-repeat');
                if (pattern) {
                    octx.fillStyle = pattern;
                    octx.fillRect(0, 0, sw, sh);
                } else {
                    octx.drawImage(upstream.image, 0, 0, sw, sh);
                }
                // Fill the remaining canvas area by stretching the source's
                // right / bottom edges so authors using clamp mode see a
                // bounded extension rather than blank space.
                if (w > sw) {
                    octx.drawImage(upstream.image, sw - 1, 0, 1, sh, sw, 0, w - sw, sh);
                }
                if (ih > sh) {
                    octx.drawImage(upstream.image, 0, sh - 1, sw, 1, 0, sh, sw, ih - sh);
                }
                if (w > sw && ih > sh) {
                    octx.drawImage(upstream.image, sw - 1, sh - 1, 1, 1, sw, sh, w - sw, ih - sh);
                }
            } else {
                // L41 default 'repeat' — canonical createPattern path.
                const pattern = octx.createPattern(upstream.image, 'repeat');
                if (pattern) {
                    octx.fillStyle = pattern;
                    octx.fillRect(0, 0, w, ih);
                } else {
                    // Fallback — shouldn't happen on modern browsers, but degrade gracefully.
                    for (let y = 0; y < rows; y++) {
                        for (let x = 0; x < cols; x++) {
                            octx.drawImage(upstream.image, x * sw, y * sh, sw, sh);
                        }
                    }
                }
            }
            return { image: off, width: w, height: ih };
        }
    }

    // C10 / F7 — standard CSS / Canvas2D blend modes. Mirrors
    // NodeEvaluator._validBlendModes so both sides stay aligned.
    const VALID_BLEND_MODES = new Set([
        'source-over',
        'multiply', 'screen', 'overlay', 'darken', 'lighten',
        'color-dodge', 'color-burn', 'hard-light', 'soft-light',
        'difference', 'exclusion', 'hue', 'saturation', 'color', 'luminosity',
    ]);

    /// Image.Combine key modes — produce a copy of `srcImage` whose alpha
    /// channel reflects the chosen rule. The composited result is then
    /// drawn over the bottom (A) input with a normal source-over op.
    ///   • alpha-key      — strip nothing, keep original alpha (premultiply
    ///                      the existing alpha into RGB so blending matches).
    ///   • luminance-key  — derive alpha from per-pixel luminance: black
    ///                      becomes transparent, white opaque.
    ///   • inv-luminance  — inverse: white becomes transparent, black opaque.
    function applyKeyMode(srcImage, w, h, mode) {
        // Sprint 83 — pooled. The single caller (evalImageCombine) composites
        // the returned canvas onto its own `off` and then calls
        // canvasPool.release() before returning. This is an escape-boundary
        // migration: the canvas leaves the function but the contract is "caller
        // releases", not "caller keeps".
        const off = canvasPool.acquire(w, h);
        const c = off.getContext('2d');
        c.drawImage(srcImage, 0, 0, w, h);
        if (mode === 'alpha-key') return off; // keep alpha as-is
        const data = c.getImageData(0, 0, w, h);
        const px = data.data;
        const inv = mode === 'inv-luminance';
        for (let i = 0; i < px.length; i += 4) {
            // Standard ITU-R BT.709 luminance weights.
            const lum = 0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2];
            const a   = inv ? (255 - lum) : lum;
            px[i + 3] = Math.max(0, Math.min(255, Math.round(a)));
        }
        c.putImageData(data, 0, 0);
        return off;
    }

    // M68 — visible cycle-detection placeholder. Renders a small red rect with
    // "ERR" so the user sees something is wrong on the OBS preview rather than
    // a silently empty widget. The placeholder doubles as the value returned
    // from evalNodeOutput on cycle so downstream nodes don't get null and crash.
    function makeErrorPlaceholder(label) {
        const w = 240, h = 60;
        const off = (typeof OffscreenCanvas !== 'undefined')
            ? new OffscreenCanvas(w, h)
            : (() => { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; })();
        const c = off.getContext('2d');
        c.fillStyle = 'rgba(180, 40, 40, 0.85)';
        c.fillRect(0, 0, w, h);
        c.fillStyle = '#ffffff';
        c.font = 'bold 18px sans-serif';
        c.textBaseline = 'middle';
        c.textAlign = 'center';
        c.fillText(`ERR: ${label}`, w / 2, h / 2);
        return { image: off, width: w, height: h, hasError: true, errorMessage: label };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    function attr(node, key, fallback) {
        return (node.Attributes && key in node.Attributes) ? node.Attributes[key] : fallback;
    }

    // ── Sweep 21: keyframe sampling ─────────────────────────────────────────
    // Mirrors KeyframeInterpolation.cs. Read by attrAnimated / attrAnimatedColor below, on BOTH
    // clocks: the design-time scrub cursor from the WebView2 bridge, and (since V5) the production
    // activation clock the animator loop writes before each frame. The OBS path no longer
    // "resolves to fallback because timeMs stays 0" — that was the defect V5 fixed.

    // WIRE-NAME FIX — dispatch on the token LayerSerializer actually emits. The .phxlayer
    // carries the KeyframeCurve enum in camelCase because LayerSerializer registers
    // JsonStringEnumConverter<KeyframeCurve>(JsonNamingPolicy.CamelCase); this dispatcher used
    // to switch on the C# PascalCase spelling, so EVERY non-linear curve fell through to
    // `default: return t` and played Linear on the OBS overlay — and the custom-handle guard
    // above the switch was unreachable, so handles dragged in CurveEditorDialog never applied.
    // Folding the token to lower case before dispatch fixes both and keeps a hand-edited
    // PascalCase .phxlayer working, since both spellings fold to the same label.
    function applyCurveKf(t, kf) {
        t = Math.max(0, Math.min(1, t));
        const rawCurve = kf && kf.curve;
        const curve = typeof rawCurve === 'string' ? rawCurve.toLowerCase() : rawCurve;
        if (curve === 'bezier' && kf.p1x != null && kf.p1y != null && kf.p2x != null && kf.p2y != null) {
            return cubicBezier(t, kf.p1x, kf.p1y, kf.p2x, kf.p2y);
        }
        switch (curve) {
            case 'linear':    return t;
            case 'easein':    return t * t;
            case 'easeout':   return 1 - (1 - t) * (1 - t);
            case 'easeinout': return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            case 'bezier':    return cubicBezier(t, 0.25, 0.1, 0.25, 1.0);
            case 'step':      return 0;
            default:          return t;
        }
    }

    function cubicBezier(x, p1x, p1y, p2x, p2y) {
        const cx = 3 * p1x;
        const bx = 3 * (p2x - p1x) - cx;
        const ax = 1 - cx - bx;
        const cy = 3 * p1y;
        const by = 3 * (p2y - p1y) - cy;
        const ay = 1 - cy - by;
        let t = x;
        for (let i = 0; i < 8; i++) {
            const xt = ((ax * t + bx) * t + cx) * t;
            const dx = (3 * ax * t + 2 * bx) * t + cx;
            if (Math.abs(dx) < 1e-9) break;
            t -= (xt - x) / dx;
            t = Math.max(0, Math.min(1, t));
        }
        return ((ay * t + by) * t + cy) * t;
    }

    /// Sample an animated scalar value at timeMs from a keyframe list.
    /// Mirror of KeyframeInterpolation.SampleScalar in C#.
    function keyframeSampleScalar(keyframes, timeMs) {
        if (!keyframes || keyframes.length === 0) return null;
        const sorted = [...keyframes].sort((a, b) => (a.time || 0) - (b.time || 0));
        if (timeMs <= (sorted[0].time || 0))     return Number(sorted[0].value);
        const last = sorted[sorted.length - 1];
        if (timeMs >= (last.time || 0))          return Number(last.value);
        for (let i = 0; i < sorted.length - 1; i++) {
            const a = sorted[i], b = sorted[i + 1];
            const at = a.time || 0, bt = b.time || 0;
            if (timeMs >= at && timeMs <= bt) {
                const span = bt - at;
                const t = span <= 0 ? 0 : (timeMs - at) / span;
                const eased = applyCurveKf(t, a);
                const va = Number(a.value);
                const vb = Number(b.value);
                return va + (vb - va) * eased;
            }
        }
        return Number(last.value);
    }

    /// Read a node attribute, overriding from the active timeline's keyframes
    /// when one matches `<node.Id>.<socketName>`. Used by kernels whose inline
    /// attributes are common animation targets (Image.Transform, Image.Scale,
    /// Scalar.Constant, Vector*.Constant).
    ///
    /// Production paths sample at an ADVANCING cursor since V5: triggerContext.timeMs is
    /// `now - activationStart` for the widget being rendered, so an authored track plays in OBS
    /// from the moment the widget was last activated. (It used to be pinned at 0 on every
    /// production path, so every keyframe resolved to its first value and the animation the author
    /// scrubbed in the editor never moved on stream. That was the V5 defect.) An un-keyframed
    /// parameter still returns the static attribute, unchanged.
    ///
    /// The C# mirror (NodeEvaluator, KeyframeInterpolation.SampleScalar) samples the same tracks at
    /// whatever cursor its caller threads in — V11 passes the editor's playhead so node-body
    /// thumbnails follow it. Same curve, same clamping; only the clock differs.
    function attrAnimated(node, key, fallback) {
        if (activeTimeline && activeTimeline.keyframes && activeTimeline.keyframes.length) {
            const path = `${node.Id}.${key}`;
            const matching = activeTimeline.keyframes.filter(k => k.parameterPath === path);
            if (matching.length > 0) {
                const sampled = keyframeSampleScalar(matching, triggerContext.timeMs || 0);
                if (sampled !== null && Number.isFinite(sampled)) return String(sampled);
            }
        }
        return attr(node, key, fallback);
    }

    // Colour-aware animated read. A colour attribute `<key>` keyframes as four
    // 0–255 scalar channel tracks at `<node.Id>.<key>.R/.G/.B/.A` (mirrors
    // AnimatedPinRegistry.GetColorChannelKeys / GetPinComponents). When any channel
    // has keyframes, sample each (a channel with no keyframes falls back to the
    // static colour's channel) and recombine to #RRGGBBAA. Un-keyframed colours
    // return the plain static value UNCHANGED — so rgba()/named defaults
    // (Image.Shadow / Image.Glow) still pass straight through to the consumer.
    function attrAnimatedColor(node, key, fallback) {
        if (activeTimeline && activeTimeline.keyframes && activeTimeline.keyframes.length) {
            const base = `${node.Id}.${key}`;
            const ks = activeTimeline.keyframes;
            const rk = ks.filter(k => k.parameterPath === base + '.R');
            const gk = ks.filter(k => k.parameterPath === base + '.G');
            const bk = ks.filter(k => k.parameterPath === base + '.B');
            const ak = ks.filter(k => k.parameterPath === base + '.A');
            if (rk.length || gk.length || bk.length || ak.length) {
                const tm = triggerContext.timeMs || 0;
                // Static colour in 0–255 channels — handles hex AND rgba()/rgb() so a
                // PARTIALLY-keyframed Image.Shadow/Glow keeps its CSS rgba() default on
                // the un-keyed channels (contract: rgba/named defaults pass through;
                // parseHexColor alone would read them as opaque white).
                const stat = parseColorChannels255(stripQuotes(attr(node, key, fallback)));
                const clampByte = n => Math.max(0, Math.min(255, Math.round(n)));
                const hex2 = n => clampByte(n).toString(16).padStart(2, '0');
                const chan = (kfs, statCh) => {
                    if (!kfs.length) return statCh;
                    const v = keyframeSampleScalar(kfs, tm);
                    return (v == null || !Number.isFinite(v)) ? statCh : v;
                };
                const r = chan(rk, stat.r);
                const g = chan(gk, stat.g);
                const b = chan(bk, stat.b);
                const a = chan(ak, stat.a);
                return '#' + hex2(r) + hex2(g) + hex2(b) + hex2(a);
            }
        }
        return stripQuotes(attr(node, key, fallback));
    }

    function stripQuotes(s) {
        if (typeof s !== 'string') return s;
        if (s.length >= 2 && s[0] === '"' && s[s.length - 1] === '"') return s.slice(1, -1);
        return s;
    }

    /// V10 — greedy word wrap for ONE row of Text.Render, measured against the caller's own
    /// context (so the font already set there is the font measured with). Returns at least one
    /// row, always, so a caller can concatenate the result without an emptiness check.
    ///
    /// A single WORD wider than the limit is emitted intact rather than broken mid-glyph-run.
    /// Character-level breaking is the wrong default for an overlay: the strings that overflow
    /// are viewer names, emote codes and URLs, and a name split across two lines is less
    /// readable than one that runs to the frame edge — where the author can see it and scale it
    /// down with an Image.Transform. This is also why Wrap ships OFF.
    function _wrapTextRow(ctx, row, maxWidth) {
        const text = String(row === null || row === undefined ? '' : row);
        if (!text) return [''];
        if (ctx.measureText(text).width <= maxWidth) return [text];
        const words = text.split(' ');
        const out = [];
        let line = '';
        for (const word of words) {
            const candidate = line ? line + ' ' + word : word;
            if (line && ctx.measureText(candidate).width > maxWidth) {
                out.push(line);
                line = word;
            } else {
                line = candidate;
            }
        }
        out.push(line);
        return out;
    }

    // F3 — Color.Constant payload parsing. Hex string (#rgb / #rrggbb / #rrggbbaa) →
    // { r, g, b, a } with components in 0..1. Anything malformed → opaque white.
    function parseHexColor(hex) {
        if (typeof hex !== 'string') return { r: 1, g: 1, b: 1, a: 1 };
        let s = hex.trim();
        if (s.startsWith('#')) s = s.slice(1);
        if (s.length === 3) s = s.split('').map(c => c + c).join('');
        if (s.length === 6 || s.length === 8) {
            const r = parseInt(s.slice(0, 2), 16);
            const g = parseInt(s.slice(2, 4), 16);
            const b = parseInt(s.slice(4, 6), 16);
            const a = s.length === 8 ? parseInt(s.slice(6, 8), 16) : 255;
            if ([r, g, b, a].every(Number.isFinite))
                return { r: r / 255, g: g / 255, b: b / 255, a: a / 255 };
        }
        return { r: 1, g: 1, b: 1, a: 1 };
    }

    // F3 — render a Color value to a CSS string for ctx.fillStyle / ctx.strokeStyle.
    // Accepts either the new {r,g,b,a} object form or a legacy hex string (passes through).
    function colorToCss(c) {
        if (typeof c === 'string') return c;
        if (!c || typeof c !== 'object') return '#ffffff';
        const r = Math.round(((c.r ?? 1)) * 255);
        const g = Math.round(((c.g ?? 1)) * 255);
        const b = Math.round(((c.b ?? 1)) * 255);
        const a = c.a ?? 1;
        return `rgba(${r}, ${g}, ${b}, ${a})`;
    }

    // True when a CSS colour string is fully transparent (alpha 0). Used by
    // Text.Render's Background so the default #00000000 paints nothing. Handles
    // #rrggbbaa / #rgba hex and rgba()/rgb(); opaque #rgb / #rrggbb / named ⇒ false.
    function _isTransparentCss(css) {
        if (!css) return true;
        const s = String(css).trim().toLowerCase();
        if (s === 'transparent') return true;
        if (s[0] === '#') {
            if (s.length === 9) return s.slice(7) === '00';   // #rrggbbaa
            if (s.length === 5) return s[4] === '0';           // #rgba
            return false;                                       // #rgb / #rrggbb opaque
        }
        const m = s.match(/^rgba?\(([^)]+)\)$/);
        if (m) {
            const parts = m[1].split(',').map(x => x.trim());
            if (parts.length === 4) return parseFloat(parts[3]) === 0;
        }
        return false;
    }

    // Parse a colour string into 0–255 {r,g,b,a} channels — handles hex (#rgb /
    // #rrggbb / #rrggbbaa) AND CSS rgb()/rgba() (alpha 0..1 → 0..255). Used by
    // attrAnimatedColor so a partially-keyframed colour whose static value is a
    // CSS rgba() default (Image.Shadow / Image.Glow) keeps that default on the
    // un-keyframed channels. Falls back to opaque white on anything unparseable.
    function parseColorChannels255(str) {
        if (typeof str === 'string') {
            const m = str.trim().match(/^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i);
            if (m) {
                return {
                    r: Math.round(Number(m[1])),
                    g: Math.round(Number(m[2])),
                    b: Math.round(Number(m[3])),
                    a: m[4] != null ? Math.round(Number(m[4]) * 255) : 255,
                };
            }
        }
        const c = parseHexColor(str); // 0..1
        return { r: c.r * 255, g: c.g * 255, b: c.b * 255, a: c.a * 255 };
    }

    // An off-DOM `new Image()` (what loadImage returns) does NOT advance its GIF
    // frames in Chromium — the browser only animates an <img> that is connected to
    // the rendering tree. So ctx.drawImage() in the per-widget animator captures
    // frame 0 forever and the GIF renders STATIC in the OBS output (the in-editor
    // WinUI preview uses a different, DOM-backed image control, which is why it
    // animates there). Attaching the GIF to a hidden in-viewport pump container
    // (opacity:0, behind everything, non-interactive) puts it in the rendering tree
    // so Chromium animates it; the animator's drawImage then samples the advancing
    // frames. The OBS browser-source document is always "visible" (it's a render
    // surface, never a backgrounded tab), so opacity:0 does not pause the animation.
    let _gifPump = null;
    function ensureGifAnimating(img) {
        if (!img || img._gifPumped) return;
        if (!_gifPump) {
            _gifPump = document.createElement('div');
            _gifPump.id = 'gif-anim-pump';
            _gifPump.setAttribute('aria-hidden', 'true');
            _gifPump.style.cssText =
                'position:fixed;left:0;top:0;width:0;height:0;overflow:visible;' +
                'opacity:0;pointer-events:none;z-index:-1;';
            document.body.appendChild(_gifPump);
        }
        img._gifPumped = true;
        try { _gifPump.appendChild(img); } catch { /* element reuse race — already attached */ }
    }

    // ── Animated-GIF frame decode (WebCodecs ImageDecoder) ──────────────────────
    // The DOM-pump above is unreliable: a GIF decoded while disconnected from the
    // DOM (or painted at opacity:0 / zero area) does not advance its frames in the
    // Chromium that backs OBS browser sources AND the WinUI WebView2 preview, so the
    // canvas stayed on frame 0 even though the WinUI node-body BitmapImage preview
    // animated. Decode the frames explicitly and let the per-widget animator sample
    // the frame for the current wall-clock time — no DOM element, no paint/visibility
    // quirks. Each frame is rasterised to its own cached canvas so downstream image-op
    // nodes (which snapshot the upstream into offscreen canvases) animate through the
    // whole pipeline. Falls back to the static <img> + legacy DOM-pump when
    // ImageDecoder is unavailable or the source turns out to be single-frame.
    const _gifDecoders = new Map(); // src -> { frames:[{canvas,durMs}], totalMs, ready, failed }
    const GIF_DECODER_MAX = 24;
    function _hasImageDecoder() { return typeof ImageDecoder !== 'undefined'; }
    function ensureGifDecoder(src) {
        let c = _gifDecoders.get(src);
        if (c) { _gifDecoders.delete(src); _gifDecoders.set(src, c); return c; } // LRU promote
        c = { frames: [], totalMs: 0, ready: false, failed: false };
        _gifDecoders.set(src, c);
        while (_gifDecoders.size > GIF_DECODER_MAX) {
            const oldest = _gifDecoders.keys().next().value;
            if (oldest === undefined || oldest === src) break;
            _gifDecoders.delete(oldest);
        }
        (async () => {
            try {
                const resp = await fetch(src, { cache: 'force-cache' });
                if (!resp.ok) throw new Error('fetch ' + resp.status);
                const buf = await resp.arrayBuffer();
                const dec = new ImageDecoder({ data: buf, type: 'image/gif' });
                await dec.tracks.ready;
                const track = dec.tracks.selectedTrack;
                const count = (track && track.frameCount) ? track.frameCount : 1;
                if (count <= 1 || (track && track.animated === false)) {
                    c.failed = true; try { dec.close(); } catch { } return;
                }
                for (let i = 0; i < count; i++) {
                    const { image } = await dec.decode({ frameIndex: i });
                    const cv = document.createElement('canvas');
                    cv.width = image.displayWidth; cv.height = image.displayHeight;
                    cv.getContext('2d').drawImage(image, 0, 0);
                    // VideoFrame.duration is MICROSECONDS; default ~100ms when absent.
                    let durMs = image.duration ? image.duration / 1000 : 100;
                    if (!(durMs > 0)) durMs = 100;
                    try { image.close(); } catch { }
                    c.frames.push({ canvas: cv, durMs });
                    c.totalMs += durMs;
                }
                try { dec.close(); } catch { }
                c.ready = c.frames.length > 1 && c.totalMs > 0;
                if (!c.ready) c.failed = true;
            } catch (e) {
                c.failed = true;
                try { debugLog('gifDecode.fail', { src, error: e && e.message }); } catch { }
            }
        })();
        return c;
    }
    function gifFrameAt(c, nowMs) {
        if (!c || !c.ready || !c.frames.length || !(c.totalMs > 0)) return null;
        let t = ((nowMs % c.totalMs) + c.totalMs) % c.totalMs;
        for (let i = 0; i < c.frames.length; i++) {
            const f = c.frames[i];
            if (t < f.durMs) return f.canvas;
            t -= f.durMs;
        }
        return c.frames[c.frames.length - 1].canvas;
    }
    // Current GIF frame canvas for `src` (kicks off decode on first call). Returns
    // null until decoded — or for a single-frame / unsupported source — so callers
    // draw the static <img> meanwhile; the animator keeps re-rendering, so frames
    // appear as soon as the decode lands.
    function currentGifFrame(src) {
        if (!_hasImageDecoder()) return null;
        const c = ensureGifDecoder(src);
        if (c.failed) return null;
        // #6 — drive the GIF playhead from the design-time timeline cursor when
        // we're in the embedded single-widget preview (widgetFilterId set: the
        // editor's SCRUB/PLAY transport pins/advances triggerContext.timeMs), so
        // the rendered frame tracks the scrub/play time instead of the free-
        // running wall clock that previously fought it.
        //
        // DEVIATION from the literal task snippet (`typeof timeMs === 'number'` is
        // ALWAYS true), restated after V5 made the production clock real: this gate
        // originally existed because production timeMs stayed 0 forever, so reading
        // it unconditionally froze every OBS GIF on frame 0. That is no longer why
        // it stays — the production clock now advances timeMs from the widget's last
        // ACTIVATION, which would re-phase every GIF on every trigger fire and idle
        // revert. A GIF's playhead is a property of the source, not of the trigger
        // that happens to be showing it, so production deliberately keeps sampling
        // performance.now() — the shipped 0.12.27 behaviour.
        //
        // The gate is `?widget=` and NOT "a design-time surface", and not the sibling
        // _productionClockOwnsWidgetTime() either. `?widget=` is where the transport was built to
        // drive one widget's GIF frame-by-frame; the whole-layer preview (?client=editor) also
        // sends SCRUB / PLAY, but there the source of truth for a GIF is still its own playhead —
        // pinning it to the scrub cursor would freeze every OTHER widget's GIF on the layer at
        // whatever cursor the widget under the playhead happens to sit at.
        const designTime = widgetFilterId != null;
        const now = (designTime && triggerContext && typeof triggerContext.timeMs === 'number')
            ? triggerContext.timeMs
            : ((typeof performance !== 'undefined' && performance.now) ? performance.now() : 0);
        return gifFrameAt(c, now);
    }

    function loadImage(src) {
        if (imageCache.has(src)) {
            // Promote on access (LRU): re-set moves the entry to the tail of
            // the Map's insertion order so the next eviction targets the
            // genuinely-coldest image.
            const cached = imageCache.get(src);
            imageCache.delete(src);
            imageCache.set(src, cached);
            return Promise.resolve(cached);
        }
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.crossOrigin = 'anonymous';
            img.onload = () => {
                imageCache.set(src, img);
                // Evict the oldest entry until we're at the cap. Map iterators
                // yield in insertion order, so the first key is the LRU one.
                while (imageCache.size > IMAGE_CACHE_MAX) {
                    const oldest = imageCache.keys().next().value;
                    if (oldest === undefined) break;
                    imageCache.delete(oldest);
                }
                resolve(img);
            };
            img.onerror = () => reject(new Error(`image load failed: ${src}`));
            img.src = src;
        });
    }
})();
