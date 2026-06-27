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

        // Telemetry hook — debug overlay surfaces this so a streamer
        // chasing memory creep can spot whether the pool is filling.
        function stats() {
            return { totalKept, totalPx, buckets: buckets.size };
        }

        return { acquire, release, stats };
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

    let layer = null;        // Layer (Phoenix.Controls.Shared.Models.Layer)
    // Bounded LRU. Map keeps insertion-order; we promote on get and evict the
    // oldest entry when we exceed IMAGE_CACHE_MAX. Without this, multi-hour
    // streams that cycle through alert images, banners, etc. grow this Map
    // unboundedly and the OBS browser source's heap walks toward OOM.
    const IMAGE_CACHE_MAX = 256;
    let imageCache = new Map(); // path|url → HTMLImageElement
    let socket = null;

    // Live captions (manifesto §4.10 CC integration). Hub's LiveCaptionService pushes
    // CAPTION_UPDATE; the Caption.LiveCaption node reads from this state object.
    const captionState = { original: '', translated: '', sourceLanguage: '', targetLanguage: '' };

    // L49 — Caption.LiveCaption emits two output sockets. The names live in C# (NodeTemplates)
    // and were previously hardcoded as inline string literals here, so any rename on the C# side
    // would silently fall through to the wrong branch. Lift them to a single source of truth.
    // BugFixSweep3_Visualist_JS_Tests asserts this constant is present.
    const CAPTION_SOCKETS = { ORIGINAL: 'Original', TRANSLATED: 'Translated' };
    const _warnedUnknownCaptionSockets = new Set();

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
        // Sweep 21 — design-time timeline cursor in milliseconds. Set by SCRUB /
        // PLAY messages from the Visualist WebView2 bridge; defaults to 0 for
        // production renders (RUN_TRIGGER, CAPTION_UPDATE) so animated parameters
        // sample at their start values.
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
    let _audioActivationGen = 1;
    function bumpAudioActivation() { _audioActivationGen++; }

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
    //   • consumesCaption — true when any node title starts with "Caption.". Drives
    //     M46's targeted re-render on CAPTION_UPDATE so widgets that don't read captions
    //     don't get re-rendered every time a caption frame arrives.
    //   • displayNode    — the resolved Display sink (with L48 dedupe + warn applied).
    // Keyed by `${widgetId}|${triggerName}`. Cleared on layer load.
    const _triggerMeta = new Map();
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

        // M46 — caption consumption flag. Title-prefix match catches every Caption.* node
        // that the widget's evaluator chain might pull from (today only Caption.LiveCaption,
        // but new Caption.* nodes added later auto-opt-in without touching this code).
        const consumesCaption = nodes.some(n => typeof n.Title === 'string' && n.Title.startsWith('Caption.'));

        meta = { displayNode, consumesCaption };
        _triggerMeta.set(key, meta);
        return meta;
    }

    /// Returns true when ANY of the widget's triggers references a Caption.* node.
    /// Used by the CAPTION_UPDATE handler to filter the renderAll() pass to only the
    /// widgets that actually depend on caption state (M46).
    function widgetConsumesCaption(widget) {
        if (!widget || !widget.triggers) return false;
        for (const trig of widget.triggers) {
            if (getTriggerMeta(widget, trig).consumesCaption) return true;
        }
        return false;
    }

    // H62 — per-widget render mutex. Without this, a CAPTION_UPDATE-driven
    // renderAll could overlap an in-flight RUN_TRIGGER render of the same widget,
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
            connectSocket();
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
        for (const [, slot] of _widgetAnimators) {
            try { cancelAnimationFrame(slot.rafId); } catch { }
        }
        _widgetAnimators.clear();
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
        const trig = w.triggers.find(t => t.name === manipulatorState.triggerName);
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
        const trig = widget.triggers.find(t => t.name === manipulatorState.triggerName)
                  || widget.triggers.find(t => t.name === 'onStartup');
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

    // Image.Crop's Rect attribute can be a Vector4 literal "Vector4(x,y,w,h)"
    // or a bare "x,y,w,h". Default to the full widget rect when missing/zero
    // so the manipulator has a usable starting frame to drag.
    function parseRect(raw, widgetRect) {
        let x = 0, y = 0, w = widgetRect.width, h = widgetRect.height;
        if (raw && typeof raw === 'string') {
            const m = raw.replace(/[^\d.\-,]/g, '').split(',').map(parseFloat);
            if (m.length === 4 && m.every(Number.isFinite)) {
                x = m[0]; y = m[1]; w = m[2]; h = m[3];
                if (w <= 0 || h <= 0) { x = 0; y = 0; w = widgetRect.width; h = widgetRect.height; }
            }
        }
        return { x, y, w, h };
    }

    // [QC50-08] Image.Crop's Rect is canonically stored as fractions (0..1) of
    // the source-image dimensions — see EvalImageCrop in NodeEvaluator.cs and
    // the 'Image.Crop' case in the Evaluator below. The manipulator runs
    // against widget-rect pixel space, so we scale fractions × widget dims for
    // display. A degenerate or missing value defaults to the full widget rect
    // (= no crop). Note the W/H ≤ 0 fallback matches parseRect() — a stored
    // zero-area rect is treated as "passthrough" rather than "1×1 pixel".
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
    // QC28-10 — split the single 64-cap drop-oldest outbox into TWO classes
    // so that control-class messages (VISUAL_COMPLETE acks etc.) can't be
    // evicted by data-class chatter (FPS heartbeats) during a long outage.
    // Control queue has a smaller cap but is flushed first (priority send).
    // Data queue keeps the original drop-oldest-when-full behavior. Classifier
    // honours an explicit { control: true } option from the caller; we also
    // default-classify messages with type VISUAL_COMPLETE / HUB_EVENT as
    // control so existing sendSocket-style callers behave correctly without
    // an explicit flag.
    let _reconnectDelayMs = 1500;
    const _outboxControl = []; // priority queue (acks etc.)
    const _outboxData    = []; // drop-oldest queue (heartbeats etc.)
    const _outboxControlMax = 32;
    const _outboxDataMax    = 64;
    const _CONTROL_TYPES = new Set(['VISUAL_COMPLETE', 'HUB_EVENT']);

    function _isControlPayload(text) {
        // Cheap front-of-string check. Only run JSON.parse if it looks like an
        // object whose first key is "type" — avoids parsing every heartbeat
        // for nothing. Defensive try/catch because the payload could be any
        // string from a future caller.
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
            if (_outboxData.length >= _outboxDataMax) _outboxData.shift();
            _outboxData.push(text);
        }
        return false;
    }

    function flushOutbox() {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        // Drain control queue first so VISUAL_COMPLETE acks are not held up
        // behind a backlog of FPS heartbeats accumulated during the outage.
        while (_outboxControl.length > 0) {
            const text = _outboxControl.shift();
            try { socket.send(text); }
            catch { _outboxControl.unshift(text); return; } // put back, retry on next flush
        }
        while (_outboxData.length > 0) {
            const text = _outboxData.shift();
            try { socket.send(text); }
            catch { _outboxData.unshift(text); return; }
        }
    }

    function connectSocket() {
        const url = `ws://${window.location.host}/hud/${encodeURIComponent(layerId)}`;
        socket = new WebSocket(url);
        socket.onopen    = () => {
            setStatus('connected');
            _reconnectDelayMs = 1500; // reset backoff on successful connect
            flushOutbox();
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
            // Hub's LayerWatcher reloaded this layer's .phxlayer. Hard reload so the page
            // refetches /api/layer/<id> and re-runs onStartup with the new graph. Clearing
            // _triggerMeta first is belt-and-braces (the page reload drops it anyway) and
            // matches L48's "re-run dedupe scan on LAYER_RELOADED" contract should the
            // reload ever be skipped in the future.
            _triggerMeta.clear();
            setStatus('reloading…');
            window.location.reload();
            return;
        }

        if (msg.type === 'RUN_TRIGGER') {
            handleRunTrigger(msg);
            return;
        }

        // Sweep 21 — design-time scrub/play coming via the WebView2 chrome.webview channel.
        // Production OBS browsers never receive these (Visualist sends only into the
        // embedded WebView2). See LayerPreviewPanel.PostScrub / PostPlay / PostStop.
        if (msg.type === 'SCRUB')     { handleScrub(msg);    return; }
        if (msg.type === 'PLAY')      { handlePlay(msg);     return; }
        if (msg.type === 'STOP_PLAY') { handleStopPlay();    return; }

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
            if (!layer || !msg.widgetId) return;
            const w = layer.widgets.find(x => x.id === msg.widgetId);
            if (!w) return;
            if (msg.rect) {
                w.rect = w.rect || {};
                if (typeof msg.rect.x      === 'number') w.rect.x      = msg.rect.x;
                if (typeof msg.rect.y      === 'number') w.rect.y      = msg.rect.y;
                if (typeof msg.rect.width  === 'number') w.rect.width  = msg.rect.width;
                if (typeof msg.rect.height === 'number') w.rect.height = msg.rect.height;
            }
            if (typeof msg.zIndex === 'number') w.zIndex = msg.zIndex;
            if (typeof msg.name   === 'string') w.name   = msg.name;
            // #4 — repaint the manipulator overlay after the re-render so the
            // handles aren't wiped by the fresh layer-canvas paint. drawManipulator
            // no-ops when no manipulator is set (OBS / non-preview path).
            renderAll()
                .then(() => drawManipulator())
                .catch(err => console.warn('WIDGET_UPDATE rerender failed:', err));
            return;
        }

        if (msg.type === 'CAPTION_UPDATE') {
            captionState.original       = msg.original       || '';
            captionState.translated     = msg.translated     || captionState.original;
            captionState.sourceLanguage = msg.sourceLanguage || '';
            captionState.targetLanguage = msg.targetLanguage || '';
            // M46 — only re-render widgets whose evaluator chain actually consumes
            // captions. The blanket renderAll() that used to run here re-evaluated every
            // widget on every caption frame (live captions can fire at >5 Hz), wasting
            // image-load + canvas work on widgets that don't read Caption.* at all.
            renderCaptionConsumers().catch(err => console.warn('caption rerender failed:', err));
            return;
        }

        if (msg.type === 'TRANSLATE_RESPONSE') {
            const resolver = pendingTranslate.get(msg.reqId);
            if (resolver) {
                pendingTranslate.delete(msg.reqId);
                resolver(msg.translated || '');
            }
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
            socket.send(JSON.stringify({ type: 'TRANSLATE_REQUEST', reqId, text, targetLang }));
        });
    }

    // Trigger lookup: layer JSON stores explicit triggers as 'onTrigger:<name>' (Visualist
    // authoring convention) but Hub's RUN_TRIGGER carries the bare '<name>' from
    // visual.trigger_queued(layer, widget, name). Match either form so 'onStartup' (no
    // prefix) and 'greet' → 'onTrigger:greet' both resolve.
    function findTrigger(widget, name) {
        if (!widget || !widget.triggers || !name) return null;
        return widget.triggers.find(t => t.name === name)
            || widget.triggers.find(t => t.name === 'onTrigger:' + name)
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
            // reuse this generation and won't replay them).
            bumpAudioActivation();
            try {
                await renderWithTransition(widget, trigger, transMs);
            } catch (e) {
                console.warn('renderWidgetTrigger failed:', e);
                sendTriggerDiagnostic(msg, 'render_error', (e && e.message) || String(e));
                sendComplete(msg);
                return;
            }
            bumpRenderCount();

            if (msg.triggerName !== 'onStartup') {
                // Hold the trigger render on screen for the trigger's
                // configured timeline duration before reverting to onStartup.
                // Without this hold the idle-loop fallback overwrites the
                // trigger's render on the next microtask, so the user sees
                // the trigger flash for microseconds (or not at all).
                //
                // Default when the timeline duration is unset / 0: 2000 ms.
                // Authors can override per trigger via the TimelinePanel
                // duration field in Visualist. Clamped to [0, 60000] so a
                // misconfigured giant value can't lock the widget queue.
                let holdMs = Number(trigger && trigger.timeline && trigger.timeline.duration) || 0;
                if (!isFinite(holdMs) || holdMs <= 0) holdMs = 2000;
                if (holdMs > 60000) holdMs = 60000;
                await new Promise(resolve => setTimeout(resolve, holdMs));

                triggerContext.triggerName   = 'onStartup';
                triggerContext.eventData     = {};
                triggerContext.eventDataJson = '{}';
                triggerContext.timestamp     = Date.now() / 1000;
                const startup = widget.triggers.find(t => t.name === 'onStartup');
                if (startup) {
                    // Reverting to idle is a fresh onStartup activation — let its
                    // audio (if any) fire once, then settle.
                    bumpAudioActivation();
                    try { await renderWithTransition(widget, startup, transMs); bumpRenderCount(); }
                    catch (e) { console.warn('idle-loop onStartup failed:', e); }
                }
            }

            sendComplete(msg);
        });
    }

    function sendComplete(msg) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        socket.send(JSON.stringify({
            type: 'VISUAL_COMPLETE',
            layerId,
            widgetId:    msg.widgetId,
            triggerName: msg.triggerName,
            waitId:      msg.waitId || '',
        }));
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

        // #6 — kill any per-widget GIF/animation rAF loop for this widget before
        // starting the design-time Play loop. Otherwise the promoted animator
        // (driven by performance.now()) keeps re-rendering in parallel with the
        // Play tick (driven by triggerContext.timeMs), and the two fight over the
        // canvas so the scrubbed/played GIF frame flickers against the free-running
        // one. handlePlay owns timeMs while it runs.
        const _existingAnimator = _widgetAnimators.get(widget.id);
        if (_existingAnimator) {
            try { cancelAnimationFrame(_existingAnimator.rafId); } catch { /* ignore */ }
            _widgetAnimators.delete(widget.id);
        }

        _playState = {
            widget,
            trigger,
            durationMs: Number(msg.durationMs) || 0,
            loop:       !!msg.loop,
            startWall:  performance.now(),
            startMs:    triggerContext.timeMs || 0,
            raf:        0,
        };
        if (_playState.durationMs <= 0) { _playState = null; return; }

        const tick = async () => {
            if (!_playState) return;
            const elapsed = performance.now() - _playState.startWall;
            let timeMs = _playState.startMs + elapsed;
            if (timeMs >= _playState.durationMs) {
                if (_playState.loop) {
                    timeMs = timeMs % _playState.durationMs;
                    // Reset start so the modulo stays sensible across long loops.
                    _playState.startWall = performance.now();
                    _playState.startMs   = timeMs;
                } else {
                    timeMs = _playState.durationMs;
                    triggerContext.timeMs = timeMs;
                    await withWidgetLock(_playState.widget.id, async () => {
                        try { await renderWidgetTrigger(_playState.widget, _playState.trigger); bumpRenderCount(); }
                        catch (e) { console.warn('[Visualist] play final-frame render failed:', e); }
                    });
                    drawManipulator();  // #4 — repaint handles after the final-frame render
                    handleStopPlay();
                    return;
                }
            }
            triggerContext.timeMs = timeMs;
            await withWidgetLock(_playState.widget.id, async () => {
                try { await renderWidgetTrigger(_playState.widget, _playState.trigger); bumpRenderCount(); }
                catch (e) { console.warn('[Visualist] play frame render failed:', e); }
            });
            drawManipulator();  // #4 — repaint handles after each play frame
            if (_playState) _playState.raf = requestAnimationFrame(tick);
        };
        _playState.raf = requestAnimationFrame(tick);
    }

    function handleStopPlay() {
        if (_playState && _playState.raf) {
            try { cancelAnimationFrame(_playState.raf); } catch { }
        }
        // #6 — when the design-time Play loop stops, also drop any per-widget
        // animator slot for the played widget so a promoted GIF animator doesn't
        // keep free-running (driven by performance.now()) after the transport
        // stopped. Without this, stopping playback leaves the GIF looping on its
        // own clock instead of holding the last played frame.
        if (_playState && _playState.widget) {
            const _slot = _widgetAnimators.get(_playState.widget.id);
            if (_slot) {
                try { cancelAnimationFrame(_slot.rafId); } catch { /* ignore */ }
                _widgetAnimators.delete(_playState.widget.id);
            }
        }
        _playState = null;
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
                     || (widget.triggers ? widget.triggers.find(t => t.name === 'onStartup') : null);
        if (!trigger) return;

        // Render the active trigger's starting frame (timeMs = 0) so the pane
        // reflects the edited trigger's idle state until the user scrubs/plays.
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = trigger.name;
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;
        triggerContext.timeMs        = 0;

        // Switching the edited trigger tab is a genuine activation — its one-shot
        // audio plays once; subsequent scrub/play frames reuse this generation.
        bumpAudioActivation();
        withWidgetLock(widget.id, async () => {
            try { await renderWidgetTrigger(widget, trigger); bumpRenderCount(); }
            catch (e) { console.warn('[Visualist] SET_ACTIVE_TRIGGER render failed:', e); }
        }).then(() => drawManipulator());  // #4 — repaint handles after the re-render
    }

    // ── Phase 9 (a) — render-rate reporting ─────────────────────────────────
    // The Hub status bar exposes a per-layer "FPS" indicator so the streamer
    // can see at a glance whether each browser-source layer is ticking. This
    // is renders-per-second rather than display-frame-rate (the canvas only
    // paints when a RUN_TRIGGER or CAPTION_UPDATE forces it; the underlying
    // browser tab still runs at the display refresh). Bumped from
    // renderWidgetTrigger / renderCaptionConsumers; flushed once per second
    // and reset.
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

    // QC28-02 — single entry points to pause/resume the per-widget rAF loops.
    // The visibilitychange handler calls these together with the FPS interval
    // so an OBS scene-cut (page hidden) fully quiesces the compositor instead
    // of continuing to burn GPU at full rAF rate. On resume, we re-promote
    // any widget whose trigger was animated by re-firing its onStartup pass
    // — that re-runs requestWidgetAnimator() through the normal eval path
    // and the per-widget animator slot is rebuilt with a fresh failure count.
    let _animationsPaused = false;
    function pauseAllAnimations() {
        if (_animationsPaused) return;
        _animationsPaused = true;
        for (const [, slot] of _widgetAnimators) {
            try { cancelAnimationFrame(slot.rafId); } catch { /* ignore */ }
            slot.rafId = 0;
        }
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
        // Re-promote every widget whose trigger was animated. We re-render
        // the same widget+trigger so the per-widget animator's eval pass runs
        // through the normal `requestWidgetAnimator()` path; an empty result
        // (no animated source) will let promoteWidgetAnimator drop the slot.
        if (layer && Array.isArray(layer.widgets)) {
            for (const [widgetId, slot] of Array.from(_widgetAnimators.entries())) {
                const w = layer.widgets.find(x => x.id === widgetId);
                if (!w) { _widgetAnimators.delete(widgetId); continue; }
                const trig = slot.trigger;
                // Reset the breaker before resuming — being hidden is not a
                // sticky failure.
                slot._consecutiveFailures = 0;
                slot._suspendLogged = false;
                withWidgetLock(w.id, async () => {
                    try { await renderWidgetTrigger(w, trig); bumpRenderCount(); }
                    catch (e) { console.warn('[compositor] resume re-render failed:', e); }
                });
            }
        }
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
                pauseAllAnimations();
            } else {
                _startFpsTimer();
                resumeAllAnimations();
            }
        });
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    async function renderAll() {
        // Guard the whole pass — a malformed/half-loaded layer (layer or
        // layer.widgets null/undefined) would otherwise throw TypeError on the
        // for-of below and abort the entire first paint. Symmetric with the
        // guard renderCaptionConsumers already has.
        if (!layer || !layer.widgets) return;
        // Track C — in single-widget preview mode the editor can pin an "active
        // trigger" (the trigger tab being edited) via SET_ACTIVE_TRIGGER. When
        // present we render THAT trigger (at the current timeMs) instead of the
        // onStartup idle state so keyframing is visible. Bare layer / OBS renders
        // and the initial paint (previewActiveTrigger === null) keep onStartup.
        const renderTriggerName = (widgetFilterId && previewActiveTrigger)
            ? previewActiveTrigger
            : 'onStartup';
        // F5 — seed trigger context for the pass so Visual.OnStartup / Visual.OnTrigger
        // nodes have a populated LayerId / Timestamp before evaluation.
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = renderTriggerName;
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;

        ctx.clearRect(0, 0, logicalW, logicalH);
        paintBackdrop({ x: 0, y: 0, width: logicalW, height: logicalH });
        for (const widget of layer.widgets) {
            if (!isWidgetVisible(widget)) continue;
            // [P1 swarm-audit 2026-05-29] guard widget.triggers — null/undefined
            // on a malformed widget; skip it rather than throwing on .find.
            if (!widget.triggers) continue;
            // Honor the active trigger when pinned (single-widget preview); fall
            // back to onStartup if the named trigger isn't present on this widget.
            const trig = findTrigger(widget, renderTriggerName)
                      || widget.triggers.find(t => t.name === 'onStartup');
            if (!trig) continue;
            // QC28-01 — symmetric with renderCaptionConsumers / handleRunTrigger which
            // both wrap per-widget renders. Without this, a single bad widget's throw
            // aborts the full first-paint of the layer (the for-loop unwinds before
            // subsequent widgets get a chance). Log + continue so the rest of the
            // layer still paints; the broken widget will just show as blank/diagnostic.
            try {
                await renderWidgetTrigger(widget, trig);
                bumpRenderCount();
            } catch (e) {
                console.error('[compositor] renderAll: widget render failed', widget && widget.id, e);
            }
        }
    }

    /// M46 — re-render only the widgets whose evaluator chain references Caption.*.
    /// Called from CAPTION_UPDATE; uses the same per-widget render mutex as
    /// handleRunTrigger so an in-flight RUN_TRIGGER pass isn't clobbered.
    async function renderCaptionConsumers() {
        if (!layer || !layer.widgets) return;
        triggerContext.layerId       = layerId;
        triggerContext.triggerName   = 'onStartup';
        triggerContext.eventData     = {};
        triggerContext.eventDataJson = '{}';
        triggerContext.timestamp     = Date.now() / 1000;

        for (const widget of layer.widgets) {
            if (!isWidgetVisible(widget)) continue;
            if (!widgetConsumesCaption(widget)) continue;
            const trig = widget.triggers.find(t => t.name === 'onStartup');
            if (!trig) continue;
            await withWidgetLock(widget.id, async () => {
                try { await renderWidgetTrigger(widget, trig); bumpRenderCount(); }
                catch (e) { console.warn('caption-consumer rerender failed:', e); }
            });
        }
    }

    /// Renders the widget's trigger graph. Visits Display first (the visual sink),
    /// then visits Visual.Complete if the graph wires one (the completion sink).
    /// Returns when both sinks have settled (or only Display if no completion node).
    async function renderWidgetTrigger(widget, trigger) {
        // Sweep 21 — bind the active timeline so attribute readers can sample
        // animated parameters at triggerContext.timeMs. Reset to null after the
        // render so non-trigger code paths (e.g. caption rerenders) don't see
        // stale state if they run between scrub frames.
        activeTimeline = trigger.timeline || null;
        // Track E — bind the trigger's master volume (0..1) so evalAudioPlay can
        // scale each Audio.Play node's level by it. Default 1 when the trigger
        // predates the property so existing layers play at their authored volume.
        activeTriggerVolume = clamp(Number(trigger.Volume ?? 1), 0, 1);
        const ev = new Evaluator(trigger.graph, widgetRenderRect(widget));

        try {
            // L48 — Display sink is now resolved via getTriggerMeta which dedupe-scans the
            // graph and warns when more than one Display node is present (memoized per
            // widget+trigger so the scan only runs on first visit / after layer reload).
            const meta = getTriggerMeta(widget, trigger);
            const display = meta.displayNode;
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
            const graphNodes = (trigger.graph && trigger.graph.Nodes) || [];
            const audioSinks = graphNodes.filter(n => n.Title === 'Audio.Play');
            for (const audioSink of audioSinks) {
                try { await ev.evalNodeOutput(audioSink.Id, ''); }
                catch (e) { console.warn('[Visualist] Audio.Play sink eval failed:', e); }
            }

            // Visual.Complete sink — if present, "visiting" it via upstream evaluation IS the
            // completion signal. The default no-op visit just resolves any upstream chain so
            // graph authors can gate completion timing (e.g., chain through a future Wait node).
            const completeSink = graphNodes.find(n => n.Title === 'Visual.Complete');
            if (completeSink) {
                // Evaluate upstream of the Complete sink so any side-effecting nodes execute.
                // We don't care about the value — reaching this point means the chain settled.
                await ev.evalAnyInputOf(completeSink);
            }

            // Per-widget animator — if any node in this trigger flagged itself as
            // animated (Video.Load, .gif Image.Load), keep redrawing this widget
            // until a different trigger arrives or the widget unbinds.
            promoteWidgetAnimator(widget, trigger);

            return true;
        } finally {
            // Sprint 7 — return every escape-canvas allocated during this
            // trigger to the pool. Runs AFTER ctx.drawImage has copied the
            // Display result's pixels into the visible context, so reuse
            // can't race the consumer. Wrapped in try/catch so a faulting
            // release (e.g. mid-shutdown when canvasPool is torn down)
            // doesn't escape the finally and mask the original error.
            try { ev.releaseEscapes(); } catch (e) { /* shutdown best-effort */ }
        }
    }

    // ── Dip-to-blank event transition ─────────────────────────────────────
    // When widget.transitionMs > 0, a trigger swap (and the idle revert) fade the
    // OLD widget content out to blank over the first half, then fade the NEW
    // content in over the second half — instead of the instant clearRect→drawImage
    // cut. transMs === 0 is the legacy instant path (byte-identical). Widget ids
    // mid-transition are held here so the per-widget animator pauses (it would
    // otherwise repaint live content over the dip; see promoteWidgetAnimator/tick).
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
            // (re-)arms the per-widget animator now the dip is finished. For a
            // static NEW this is an identical redraw; for video/GIF it starts the
            // rAF loop that the paused animator couldn't.
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

    function resolveMediaPath(p) {
        if (!p) return p;
        if (p.startsWith('/') || /^https?:/i.test(p) || p.startsWith('data:')) return p;
        return `/media/${p.split('/').map(encodeURIComponent).join('/')}`;
    }

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
    // nodeId → the _audioActivationGen value that last (re)started this node. A
    // playback is (re)started only when the current generation differs from this,
    // so the animator / Play loop / scrub re-renders within one activation never
    // replay a finished one-shot. Cleared on layer teardown (_disposeGlobals).
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
        // Re-source only when the path actually changes — same-source replays
        // would otherwise restart on every render tick.
        if (a.dataset.src !== src) {
            a.dataset.src = src;
            a.src = src;
            try { a.load(); } catch { }
        }
        a.volume = opts.volume;
        a.loop   = opts.loop;

        if (!newActivation) {
            // Re-render within the SAME activation. For a looping clip a.loop keeps
            // it going natively; for a one-shot we must not restart it. Either way,
            // never call play() again here — just keep the level in sync (the
            // trigger master volume can change between render ticks).
            return;
        }
        // Genuine (re)activation for this node — stamp the generation and (re)start.
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

    // Per-widget animator — set during evaluation when Video.Load / animated GIF
    // / Particles.Emit is encountered, then promoted to a rAF loop after the
    // render that discovered it. Cancelled when a different trigger renders
    // into the widget.
    let _animatorRequestForCurrentRender = false;
    function requestWidgetAnimator() { _animatorRequestForCurrentRender = true; }

    // Sprint 92 — Particles.Emit per-node state. Each Particles.Emit node keeps
    // its own active particle list + last-tick timestamp here, keyed by node id.
    // Survives across renders so particles flow continuously between triggers
    // (the per-widget rAF animator promoted by requestWidgetAnimator() drives
    // the re-render). Capped at PARTICLE_HARD_CAP per node to keep a runaway
    // Rate from OOMing the browser when an author types 99999.
    const PARTICLE_HARD_CAP = 500;
    const _particleState = new Map(); // nodeId → { particles: [...], lastTickMs: number }

    const _widgetAnimators = new Map(); // widgetId → { rafId, widget, trigger }
    function promoteWidgetAnimator(widget, trigger) {
        // #6 — while the design-time Play loop owns this widget, IT is the
        // re-render driver (its rAF tick advances triggerContext.timeMs and
        // re-renders). Promoting a separate per-widget animator here would
        // re-create exactly the competing free-running loop the fix removes:
        // renderWidgetTrigger runs on every Play frame and would otherwise
        // re-add the slot via requestWidgetAnimator() one frame after handlePlay
        // deleted it. Skip promotion (and consume the per-render request flag so
        // it doesn't leak into the next non-Play render) when Play drives this
        // widget. handleStopPlay drops the slot so the GIF holds its last frame.
        if (_playState && _playState.widget && _playState.widget.id === widget.id) {
            _animatorRequestForCurrentRender = false;
            return;
        }
        // A dip-to-blank transition owns this widget right now — don't promote a
        // competing animator that would repaint live content over the fade. The
        // final render in renderWithTransition re-promotes once the dip completes.
        if (_widgetTransitions.has(widget.id)) {
            _animatorRequestForCurrentRender = false;
            return;
        }
        const existing = _widgetAnimators.get(widget.id);
        if (!_animatorRequestForCurrentRender) {
            // No animated source on this trigger — stop any prior animator.
            if (existing) {
                try { cancelAnimationFrame(existing.rafId); } catch { }
                _widgetAnimators.delete(widget.id);
            }
            return;
        }
        _animatorRequestForCurrentRender = false;

        // Restart if the trigger changed; otherwise leave the existing loop running.
        if (existing && existing.trigger === trigger) return;
        if (existing) {
            try { cancelAnimationFrame(existing.rafId); } catch { }
        }
        // QC28-03 — circuit breaker. A broken widget previously spammed the
        // console at full rAF rate forever; trip after N consecutive throws
        // so a single broken widget can't pin a core and flood logs.
        const slot = { rafId: 0, widget, trigger, _consecutiveFailures: 0, _suspendLogged: false };
        const MAX_CONSECUTIVE_FAILURES = 5;
        const tick = async () => {
            const cur = _widgetAnimators.get(widget.id);
            if (!cur || cur !== slot) return;
            // Paused while a dip-to-blank transition owns the widget — keep the
            // loop alive (so it resumes if it isn't replaced) but don't repaint
            // over the fade. renderWithTransition's final render re-promotes.
            if (_widgetTransitions.has(widget.id)) {
                slot.rafId = requestAnimationFrame(tick);
                return;
            }
            try {
                await renderWidgetTrigger(widget, trigger);
                slot._consecutiveFailures = 0;
            }
            catch (e) {
                slot._consecutiveFailures++;
                console.warn('[Visualist] widget animator render failed:', e);
                if (slot._consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
                    if (!slot._suspendLogged) {
                        console.error('[Visualist] suspending widget animator after',
                            MAX_CONSECUTIVE_FAILURES, 'consecutive failures; widget=', widget && widget.id);
                        slot._suspendLogged = true;
                    }
                    // Stop scheduling — leave slot in the map so a future
                    // promoteWidgetAnimator() with a different trigger can replace it.
                    return;
                }
            }
            if (_widgetAnimators.get(widget.id) === slot)
                slot.rafId = requestAnimationFrame(tick);
        };
        slot.rafId = requestAnimationFrame(tick);
        _widgetAnimators.set(widget.id, slot);
    }

    // ── Graph evaluator ──────────────────────────────────────────────────────

    class Evaluator {
        constructor(graph, frame) {
            this.graph    = graph;
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
            // Once-per-fire dedup for Result.If's missing-arg diagnostic. The
            // Evaluator is instantiated per renderWidgetTrigger call, so the Set's
            // lifetime is exactly one trigger fire — matching the C# side's
            // _loggedMissingArgs ThreadStatic that resets per Evaluate() call.
            this.loggedMissingArgs = new Set();
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
                case 'Caption.LiveCaption': value = this.evalCaptionLive(node, socketId); break;
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
                    const sx = parseFloat(attrAnimated(node, 'ScaleX', '1')) || 1;
                    const sy = parseFloat(attrAnimated(node, 'ScaleY', '1')) || 1;
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
                case 'Image.Blend':       value = await this.evalImageBlend(node);       break;
                case 'Image.Combine':     value = await this.evalImageCombine(node);     break;
                case 'Image.Tile':        value = await this.evalImageTile(node);        break;

                // Result.If — gate the upstream In image based on a comparison between
                // triggerContext.eventData[When] (e.g. "Args1") and the Equals attribute
                // (or wired Equals input). On match → pass In through. On mismatch or
                // when the named arg is missing → emit null so Display sees nothing
                // flowing into this branch. Per-fire missing-arg diagnostic via
                // sendTriggerDiagnostic, deduped on this.loggedMissingArgs.
                case 'Result.If':         value = await this.evalResultIf(node);         break;

                // Sprint 91 — WebSource runtime: fetch + image-content-type
                // only. If the URL returns image/* the proxied bytes flow
                // through Image.LoadUrl's same loadImage path (browser-cached
                // + LRU-evicted). HTML pages are NOT supported in this slice
                // — UrlImageCache rejects non-image MIME types at validation
                // and the loadImage promise rejects, surfacing a clear
                // WebSource-specific console.warn.
                case 'WebSource':         value = await this.evalWebSource(node);    break;
                // Sprint 92 — Particles.Emit per-widget rAF runtime.
                // Tick-based emitter, sprite render, requestWidgetAnimator
                // hooks the widget into a continuous rAF loop so
                // particles flow between triggers.
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

                // Track D — Convert nodes (scalar↔string, RGBA→Color, hex→Color).
                case 'Convert.NumberToString': value = await this.evalConvertNumberToString(node); break;
                case 'Convert.StringToNumber': value = await this.evalConvertStringToNumber(node); break;
                case 'Convert.ColorFromRGBA':  value = await this.evalConvertColorFromRGBA(node);  break;
                case 'Convert.HexToColor':     value = await this.evalConvertHexToColor(node);     break;

                // Track D — Message.Read: the read-out node for the transmitted
                // message. Reads triggerContext.eventData[Key] with MockValue fallback.
                case 'Message.Read':      value = this.evalMessageRead(node);         break;

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
            const sock = node.Sockets.find(s => s.Name === socketName && s.Type === 0); // 0 = Input
            if (!sock) return null;
            return this.graph.Links.find(l => l.ToNodeId === nodeId && l.ToSocketId === sock.Id);
        }

        async evalImageLoad(node) {
            const path = stripQuotes(attr(node, 'Path', ''));
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
        // a continuous rAF loop so the state ticks between author triggers
        // — the decision A2b "per-widget rAF opt-in" model. Particles
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
            octx.fillStyle = color;
            for (let i = 0; i < state.particles.length; i++) {
                const p = state.particles[i];
                const lifeFrac = 1 - (p.age / p.lifetime);
                octx.globalAlpha = Math.max(0, Math.min(1, lifeFrac));
                octx.beginPath();
                octx.arc(p.x * w, p.y * h, baseRadius, 0, Math.PI * 2);
                octx.fill();
            }
            octx.globalAlpha = 1;
            return { image: off, width: w, height: h };
        }

        async evalVideoLoad(node, socketId) {
            const path = stripQuotes(attr(node, 'Path', ''));
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

        async evalAudioLoad(node) {
            const path = stripQuotes(attr(node, 'Path', ''));
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
            const nodeVolume = Math.max(0, Math.min(1, parseFloat(attr(node, 'Volume', '1')) || 1));
            // Track E — scale the node's own Volume by the active trigger's master
            // volume (default 1) and re-clamp to 0..1 so the mixer can attenuate all
            // audio in the trigger without touching per-node attributes.
            const volume = Math.max(0, Math.min(1, nodeVolume * activeTriggerVolume));
            const loop   = String(attr(node, 'Loop', 'false')).toLowerCase() === 'true';
            // Pass the current activation generation so a one-shot fires once per
            // genuine trigger, not once per animator/Play/scrub render tick.
            ensureAudioElementAndPlay(node.Id, upstream.src, { volume, loop, gen: _audioActivationGen });
            return null;
        }

        async evalImageScale(node) {
            const inLink = this.findLinkTo(node.Id, 'In');
            if (!inLink) return null;
            const upstream = await this.evalNodeOutput(inLink.FromNodeId, inLink.FromSocketId);
            if (!upstream || !upstream.image) return upstream;

            const factorLink = this.findLinkTo(node.Id, 'Factor');
            // Sweep 21 — Factor is a canonical animation target; honor keyframes.
            let factor = parseFloat(attrAnimated(node, 'Factor', '1')) || 1;
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
            return {
                image:  upstream.image,
                width:  Math.round(upstream.width  * factor),
                height: Math.round(upstream.height * factor),
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

        /// Caption.LiveCaption emits two outputs — Original (raw) and Translated (post-translator).
        /// Compositor.js can't see which output the caller asked for from socketId alone, so we
        /// look up the node's socket by id and dispatch on its name. L49 — names sourced from
        /// CAPTION_SOCKETS so a C# rename can be tracked in one place; an unrecognized socket
        /// name is warned about (once per name) instead of silently falling through.
        evalCaptionLive(node, socketId) {
            const sock = node.Sockets.find(s => s.Id === socketId);
            if (!sock) return captionState.original;
            if (sock.Name === CAPTION_SOCKETS.TRANSLATED) {
                return captionState.translated || captionState.original;
            }
            if (sock.Name === CAPTION_SOCKETS.ORIGINAL) {
                return captionState.original;
            }
            if (!_warnedUnknownCaptionSockets.has(sock.Name)) {
                _warnedUnknownCaptionSockets.add(sock.Name);
                console.warn(
                    `[Visualist] Caption.LiveCaption: unrecognized socket name "${sock.Name}" — ` +
                    `expected one of ${JSON.stringify(Object.values(CAPTION_SOCKETS))}. ` +
                    'Falling back to Original. Check NodeTemplates.cs vs CAPTION_SOCKETS in compositor.js.');
            }
            return captionState.original;
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
            c.font         = `${fontSize}px ${fontFam}`;
            c.fillStyle    = color;
            c.textBaseline = 'middle';
            c.textAlign    = alignment === 'left' ? 'left' : alignment === 'right' ? 'right' : 'center';
            // Horizontal: honour Alignment within the frame, inset off the edge for
            // left/right. Vertical: centred — Image.Transform's TranslateY repositions it
            // from there. Single line; overflow clips at the frame edge (or the author
            // scales it down with an Image.Transform).
            const pad   = Math.max(4, Math.round(fontSize * 0.15));
            const textX = alignment === 'left' ? pad : alignment === 'right' ? (fw - pad) : (fw / 2);
            // [text-stroke 2026-06-10] Draw the outline FIRST so the fill sits on
            // top of the inner half of the centred stroke — the standard crisp-
            // outline technique. lineJoin/miterLimit keep glyph corners rounded
            // instead of spiking. lineWidth is in LOGICAL px (we're inside the
            // c.scale(SS,SS) frame) so it matches the author's "width in px".
            if (strokeWidth > 0) {
                c.lineJoin    = 'round';
                c.miterLimit  = 2;
                c.lineWidth   = strokeWidth;
                c.strokeStyle = strokeColor;
                c.strokeText(text, textX, fh / 2);
            }
            c.fillText(text, textX, fh / 2);

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

        _reportMissingArg(node, when, kind) {
            const key = `${node.Id}|${when}|${kind}`;
            if (this.loggedMissingArgs.has(key)) return;
            this.loggedMissingArgs.add(key);
            const code = kind === 'result_if' ? 'args_missing_branch' : 'args_missing_text';
            try {
                if (socket && socket.readyState === WebSocket.OPEN) {
                    socket.send(JSON.stringify({
                        type:        'TRIGGER_DIAGNOSTIC',
                        layerId:     triggerContext.layerId,
                        triggerName: triggerContext.triggerName,
                        widgetId:    null,
                        reason:      code,
                        detail:      `node=${node.Id} when='${when}'`,
                    }));
                }
            } catch (_) { /* best-effort; eval pass must continue */ }
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

        // Time / animation — timeMs is in milliseconds; convert to seconds. Production
        // renders keep timeMs=0, so these resolve to their start value (matching the
        // design-time C# t=0 mirror in NodeEvaluator).
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
            const mode = stripQuotes(attr(node, 'Mode', 'EaseInOut'));
            switch (mode) {
                case 'Linear':    return t;
                case 'EaseIn':    return t * t;
                case 'EaseOut':   return 1 - (1 - t) * (1 - t);
                case 'EaseInOut': return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
                case 'Step':      return 0;
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
        // TODO(thumbnail): manifesto §4.6 calls for a live thumbnail render here.
        // For now this is a transparent passthrough so Viewer can be dropped onto a wire
        // without breaking the chain.
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
            for (let y = 0; y < ih; y++) {
                const dx = amp * Math.sin(twoPiF * y / Math.max(1, ih));
                octx.drawImage(src, 0, y, w, 1, dx, y, w, 1);
            }
            if (mode === 'ripple') {
                // Second pass: re-stage the wave-shifted output and bow it
                // along Y by sampling each column with a sin offset. Sprint 83 —
                // `stage` is also pooled (read column-by-column, never returned).
                const stage = canvasPool.acquire(w, ih);
                stage.getContext('2d').drawImage(off, 0, 0, w, ih);
                octx.clearRect(0, 0, w, ih);
                for (let x = 0; x < w; x++) {
                    const dy = amp * Math.sin(twoPiF * x / Math.max(1, w));
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

        _readBool(node, key) {
            const raw = (attr(node, key, 'false') || 'false').toString().toLowerCase().trim();
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
    // Mirrors KeyframeInterpolation.cs for design-time scrub via the WebView2
    // bridge. Read by attrAnimated below; OBS path (no SCRUB/PLAY message)
    // resolves to fallback because triggerContext.timeMs stays 0.

    function applyCurveKf(t, kf) {
        t = Math.max(0, Math.min(1, t));
        const curve = kf && kf.curve;
        if (curve === 'Bezier' && kf.p1x != null && kf.p1y != null && kf.p2x != null && kf.p2y != null) {
            return cubicBezier(t, kf.p1x, kf.p1y, kf.p2x, kf.p2y);
        }
        switch (curve) {
            case 'Linear':    return t;
            case 'EaseIn':    return t * t;
            case 'EaseOut':   return 1 - (1 - t) * (1 - t);
            case 'EaseInOut': return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            case 'Bezier':    return cubicBezier(t, 0.25, 0.1, 0.25, 1.0);
            case 'Step':      return 0;
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
    /// Scalar.Constant, Vector*.Constant). Production paths (no SCRUB) sample
    /// at timeMs=0 which yields the first keyframe's value, equivalent to the
    /// static attribute for un-keyframed parameters.
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
        // DEVIATION from the literal task snippet (`typeof timeMs === 'number'`
        // is ALWAYS true): in OBS / layer mode triggerContext.timeMs stays at its
        // 0 default forever (no SCRUB/PLAY is ever sent and the per-widget rAF
        // animator does not bump it), so an unconditional timeMs would freeze
        // every production GIF on frame 0 — a silent regression of the shipped
        // 0.12.27 WebCodecs GIF animation. Gating on widgetFilterId (the `?widget=`
        // param only the embedded preview sets) keeps OBS on performance.now()
        // and fixes the design-time scrub case the defect is actually about.
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
