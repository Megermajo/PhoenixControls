/* Phoenix Controls — Architect Node Reference: renderer (dependency-free)
 *
 * A faithful, framework-free port of the Claude Design prototype's three JSX
 * scripts (arch-nodes-data.jsx / arch-primer.jsx / arch-nodes-page.jsx). The
 * static design preview used React + Babel-standalone from a CDN; this page is
 * shipped inside a desktop app and must render offline, so the same DOM is
 * built here with plain DOM calls and the same CSS (doc-viewer.css).
 *
 * Data contract — the host (DocViewerWindow) injects `window.PHX` BEFORE this
 * script runs, via CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync:
 *
 *   window.PHX = {
 *     META:    { categories, nodes, version },
 *     SOCKETS: { <kind>: { color, shape } },           // shape: chevron|circle|triangle|diamond|square
 *     USER_FIELDS: [ { n, k, d } ],                     // standard {user.*} struct
 *     NODE_CATALOG: [ { cat, color, tint, blurb, badge?, nodes: [
 *         { name, summary, ins:[{k,n,v?,opt?,fields?}], outs:[...],
 *           description, example?, since?, badge?, sub? }   // description/example/blurb = HTML strings
 *     ] } ],
 *     PRIMER:  { tokenFamilies:[{root,dot,label,note,toks:[]}], storageRoots:[{root,d,ex}],
 *                systemToks:[], streamToks:[], hotkeyGroups:[{sec,note?,rows:[[k,d]]}] },
 *     INITIAL_ANCHOR: "n-chat-message" | null            // deep-link target for F1-from-node
 *   }
 *
 * If `window.PHX` is absent (e.g. opened directly in a browser for QA), a small
 * placeholder is shown rather than a blank page.
 */
(function () {
  "use strict";

  // ── tiny hyperscript helpers ──────────────────────────────────────
  function h(tag, props) {
    var el = document.createElement(tag);
    if (props) {
      for (var k in props) {
        if (!Object.prototype.hasOwnProperty.call(props, k)) continue;
        var v = props[k];
        if (v == null) continue;
        if (k === "class") el.className = v;
        else if (k === "html") el.innerHTML = v;
        else if (k === "style") el.setAttribute("style", v);
        else if (k === "dataset") { for (var d in v) el.dataset[d] = v[d]; }
        else if (k.indexOf("on") === 0 && typeof v === "function") el.addEventListener(k.slice(2).toLowerCase(), v);
        else el.setAttribute(k, v);
      }
    }
    for (var i = 2; i < arguments.length; i++) append(el, arguments[i]);
    return el;
  }
  function append(el, child) {
    if (child == null || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { append(el, c); }); return; }
    el.appendChild(child.nodeType ? child : document.createTextNode(String(child)));
  }
  var SVGNS = "http://www.w3.org/2000/svg";
  function s(tag, attrs) {
    var el = document.createElementNS(SVGNS, tag);
    if (attrs) for (var k in attrs) if (attrs[k] != null) el.setAttribute(k, attrs[k]);
    // String / number children must become SVG text nodes — appendChild rejects
    // a raw string ("parameter 1 is not of type 'Node'"), which is what the glyph
    // builders (e.g. <text> labels in threeMoves) pass. Mirror h()'s text handling.
    function addChild(x) {
      if (x == null || x === false) return;
      el.appendChild(x.nodeType ? x : document.createTextNode(String(x)));
    }
    for (var i = 2; i < arguments.length; i++) {
      var c = arguments[i];
      if (c == null) continue;
      if (Array.isArray(c)) c.forEach(addChild);
      else addChild(c);
    }
    return el;
  }
  function slug(str) { return String(str).replace(/[^a-z0-9]+/gi, "-").toLowerCase().replace(/^-|-$/g, ""); }

  var PHX = window.PHX;
  if (!PHX || !PHX.NODE_CATALOG) {
    document.getElementById("nodes-root").innerHTML =
      '<div class="no-matches">Node data was not injected by the host.<br>' +
      'Open this page from <b>Architect → Help → Node Reference</b> (or press <b>F1</b> on a node).</div>';
    return;
  }
  var SOCKETS = PHX.SOCKETS || {};
  var USER_FIELDS = PHX.USER_FIELDS || [];

  // ── socket glyph (shared shape language with the editor) ───────────
  function pin(kind, opt, size) {
    var def = SOCKETS[kind] || SOCKETS.object || { color: "#A89683", shape: "circle" };
    var c = def.color, fill = opt ? "transparent" : c, sw = 1.4, z = size || 14;
    var svg = s("svg", { width: z, height: z, viewBox: "0 0 14 14" });
    var shape;
    if (def.shape === "chevron") shape = s("path", { d: "M2 2 L11 7 L2 12 Z", fill: fill, stroke: c, "stroke-width": sw, "stroke-linejoin": "round" });
    else if (def.shape === "triangle") shape = s("path", { d: "M7 2 L12 12 L2 12 Z", fill: fill, stroke: c, "stroke-width": sw });
    else if (def.shape === "diamond") shape = s("path", { d: "M7 2 L12 7 L7 12 L2 7 Z", fill: fill, stroke: c, "stroke-width": sw });
    else if (def.shape === "square") shape = s("rect", { x: 2.5, y: 2.5, width: 9, height: 9, fill: fill, stroke: c, "stroke-width": sw });
    else shape = s("circle", { cx: 7, cy: 7, r: 4.5, fill: fill, stroke: c, "stroke-width": sw });
    svg.appendChild(shape);
    return svg;
  }

  // ════════════════════════════════════════════════════════════════
  //  PRIMER — "Using the Architect"
  // ════════════════════════════════════════════════════════════════
  var MN = { HEADER: 22, TITLE: 30, TOPPAD: 8, ROW: 26 };
  function rowCY(i) { return MN.HEADER + MN.TITLE + MN.TOPPAD + i * MN.ROW + MN.ROW / 2; }

  function miniNode(o) {
    var rows = Math.max(o.ins.length, o.outs.length, 1);
    var rowEls = [];
    for (var i = 0; i < rows; i++) {
      var ip = o.ins[i], op = o.outs[i];
      var left = h("div", { style: "display:flex;align-items:center;gap:6px;position:relative;min-width:0" });
      if (ip) {
        left.appendChild(h("span", { style: "position:absolute;left:-14px;display:inline-flex" }, pin(ip.k, ip.opt, 13)));
        left.appendChild(h("span", { style: "margin-left:4px;font-size:10.5px;color:var(--coal-8);white-space:nowrap;overflow:hidden;text-overflow:ellipsis" },
          ip.n || h("em", { style: "color:var(--coal-6);font-style:normal" }, "flow")));
        if (ip.v) left.appendChild(h("span", { style: "margin-left:auto;font-family:var(--font-mono);font-size:9.5px;background:var(--coal-0);border:1px solid var(--coal-4);border-radius:2px;padding:0 4px;color:var(--ember-200);max-width:96px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" }, ip.v));
      }
      var right = h("div", { style: "display:flex;align-items:center;gap:6px;justify-content:flex-end;position:relative;min-width:0" });
      if (op) {
        right.appendChild(h("span", { style: "font-size:10.5px;color:var(--coal-8);white-space:nowrap" },
          op.n || h("em", { style: "color:var(--coal-6);font-style:normal" }, "flow")));
        right.appendChild(h("span", { style: "position:absolute;right:-14px;display:inline-flex" }, pin(op.k, false, 13)));
      }
      rowEls.push(h("div", { style: "height:" + MN.ROW + "px;display:grid;grid-template-columns:1fr 1fr;padding:0 8px;gap:8px;align-items:center;position:relative" }, left, right));
    }
    var header = h("div", { style: "height:" + MN.HEADER + "px;padding:0 10px;background:linear-gradient(180deg," + o.color + "," + o.color + "AA);border-top-left-radius:5px;border-top-right-radius:5px;border-bottom:1px solid rgba(0,0,0,0.4);display:flex;align-items:center;gap:6px" },
      h("span", { style: "font-size:8.5px;letter-spacing:0.12em;text-transform:uppercase;color:rgba(255,255,255,0.78);font-weight:700" }, o.category),
      o.badge ? h("span", { style: "margin-left:auto;font-size:8px;background:rgba(0,0,0,0.35);color:var(--ember-100);padding:1px 5px;border-radius:2px;font-family:var(--font-mono)" }, o.badge) : null);
    return h("div", { style: "position:absolute;left:" + o.x + "px;top:" + o.y + "px;width:" + o.w + "px;background:var(--coal-2);border:1px solid " + (o.selected ? "var(--ember-300)" : "var(--coal-5)") + ";border-radius:6px;box-shadow:" + (o.selected ? "var(--sh-glow)" : "var(--sh-card)") + ";font-family:var(--font-sans);user-select:none" },
      header,
      h("div", { style: "height:" + MN.TITLE + "px;display:flex;align-items:center;padding:0 12px;border-bottom:1px solid var(--coal-3);color:var(--coal-10);font-size:13px;font-weight:600;letter-spacing:-0.01em" }, o.title),
      h("div", { style: "padding-top:" + MN.TOPPAD + "px;padding-bottom:8px" }, rowEls));
  }
  function miniWire(from, to, kind, pulsing) {
    var c = (SOCKETS[kind] || SOCKETS.flow).color;
    var dx = Math.max(40, Math.abs(to.x - from.x) * 0.5);
    var path = "M " + from.x + " " + from.y + " C " + (from.x + dx) + " " + from.y + ", " + (to.x - dx) + " " + to.y + ", " + to.x + " " + to.y;
    var g = s("g", null, s("path", { d: path, stroke: c, "stroke-width": "2", fill: "none", opacity: "0.9" }));
    if (pulsing) g.appendChild(s("circle", { r: "3.5", fill: c }, s("animateMotion", { dur: "1.6s", repeatCount: "indefinite", path: path })));
    return g;
  }
  function colLabel(x, w, n, t, sub) {
    return h("div", { style: "position:absolute;left:" + x + "px;top:0;width:" + w + "px;text-align:center" },
      h("div", { style: "display:inline-flex;align-items:center;gap:7px" },
        h("span", { class: "pr-step-num" }, n),
        h("span", { style: "font-family:var(--font-display);font-size:17px;color:var(--coal-10);letter-spacing:0.02em" }, t)),
      h("div", { style: "font-family:var(--font-mono);font-size:10px;color:var(--coal-7);margin-top:1px" }, sub));
  }
  function flowDiagram() {
    var chat = { x: 18, y: 44, w: 196 }, sw = { x: 322, y: 92, w: 178 }, send = { x: 612, y: 56, w: 200 };
    var P = {
      chatFlow: { x: chat.x + chat.w, y: chat.y + rowCY(0) }, chatMsg: { x: chat.x + chat.w, y: chat.y + rowCY(1) },
      swFlow: { x: sw.x, y: sw.y + rowCY(0) }, swVal: { x: sw.x, y: sw.y + rowCY(1) }, swPing: { x: sw.x + sw.w, y: sw.y + rowCY(0) },
      sendFlow: { x: send.x, y: send.y + rowCY(0) }
    };
    var svg = s("svg", { style: "position:absolute;inset:0;width:100%;height:100%;pointer-events:none;overflow:visible" },
      miniWire(P.chatFlow, P.swFlow, "flow", true), miniWire(P.chatMsg, P.swVal, "string", false), miniWire(P.swPing, P.sendFlow, "flow", true));
    var stageInner = h("div", { style: "position:relative;width:830px;height:280px" },
      colLabel(18, 196, "1", "Trigger", "an event happens"),
      colLabel(322, 178, "2", "Logic", "branch / transform"),
      colLabel(612, 200, "3", "Action", "something fires"),
      svg,
      miniNode({ x: chat.x, y: chat.y, w: chat.w, title: "Chat.Message", category: "Events", color: "#7A2E2E", ins: [], outs: [{ k: "flow", n: "" }, { k: "string", n: "Message" }, { k: "user", n: "User" }, { k: "bool", n: "IsCommand" }] }),
      miniNode({ x: sw.x, y: sw.y, w: sw.w, title: "Logic.Switch", category: "Flow Control", color: "#3A4257", ins: [{ k: "flow", n: "" }, { k: "string", n: "Value" }], outs: [{ k: "flow", n: "Case A" }, { k: "flow", n: "Default" }] }),
      miniNode({ x: send.x, y: send.y, w: send.w, selected: true, title: "Chat.Send", category: "Platforms", color: "#7A4710", ins: [{ k: "flow", n: "" }, { k: "string", n: "Message", v: "@{user.name} pong" }], outs: [{ k: "flow", n: "" }] }));
    var cap = h("p", { class: "pr-cap", html:
      'The whole suite reads <b>left &rarr; right</b>: an event fires on the left, logic in the middle decides what matters, ' +
      'an action on the right does the thing. This is the <b>!ping &rarr; pong</b> flow &mdash; the white ' +
      '<span class="pr-inline-pin" data-pin="flow"></span><b>flow</b> wire carries execution; the amber ' +
      '<span class="pr-inline-pin" data-pin="string"></span><b>string</b> wire carries the message text into the branch.' });
    inlinePins(cap);
    return h("div", { class: "pr-stage", role: "img", "aria-label": "A flow: Chat Message trigger wired through a Switch into Send Message" },
      h("div", { class: "pr-stage-scroll" }, stageInner), cap);
  }
  function inlinePins(root) {
    root.querySelectorAll("[data-pin]").forEach(function (span) { span.appendChild(pin(span.dataset.pin, false, 14)); });
  }
  function nodeAnatomy() {
    var node = { x: 70, y: 40, w: 210 };
    function badge(left, top, n) {
      return h("span", { style: "position:absolute;left:" + left + "px;top:" + top + "px;width:19px;height:19px;border-radius:50%;background:var(--ember-400);color:var(--coal-0);font-family:var(--font-mono);font-size:11px;font-weight:700;display:flex;align-items:center;justify-content:center;box-shadow:0 2px 6px rgba(0,0,0,0.5);z-index:3" }, n);
    }
    var stage = h("div", { class: "pr-anatomy-stage" },
      h("div", { style: "position:relative;width:350px;height:200px" },
        miniNode({ x: node.x, y: node.y, w: node.w, selected: true, title: "Chat.Send", category: "Platforms", color: "#7A4710", ins: [{ k: "flow", n: "" }, { k: "string", n: "Message", v: "@{user.name} pong" }], outs: [{ k: "flow", n: "" }] }),
        badge(node.x + node.w - 26, node.y + 2, "1"),
        badge(node.x + node.w - 26, node.y + MN.HEADER + 6, "2"),
        badge(node.x - 24, node.y + rowCY(1) - 9, "3"),
        badge(node.x + 86, node.y + rowCY(1) - 9, "4"),
        badge(node.x + node.w + 4, node.y + rowCY(0) - 9, "5")));
    var keyLi = [
      ["1", "<b>Category strip</b> &mdash; colour-coded family (red = event triggers, bronze = outbound actions, slate = logic). Tells you at a glance what a node belongs to."],
      ["2", "<b>Node title</b> &mdash; the <code>Namespace.Name</code> you spawn. Search it in the spawn palette or this reference."],
      ["3", "<b>Inputs (left)</b> &mdash; wire data and execution <em>in</em>. A solid pin is required; a hollow one is optional."],
      ["4", "<b>Default chip</b> &mdash; a literal value typed in the inspector when nothing is wired. Templates like <code>{user.name}</code> resolve at run-time."],
      ["5", "<b>Outputs (right)</b> &mdash; wire execution and results <em>out</em> to the next node."]
    ].map(function (r) { return h("li", null, h("span", { class: "pr-k-num" }, r[0]), h("div", { html: r[1] })); });
    return h("div", { class: "pr-card pr-anatomy" }, stage, h("ol", { class: "pr-anatomy-key" }, keyLi));
  }
  function glyphSpawn() {
    return s("svg", { viewBox: "0 0 120 80", class: "pr-glyph" },
      s("rect", { x: 1, y: 1, width: 118, height: 78, rx: 4, fill: "none", stroke: "var(--coal-4)", "stroke-dasharray": "3 4" }),
      s("g", { "font-family": "var(--font-mono)", "font-size": "8", fill: "var(--coal-8)" },
        s("rect", { x: 40, y: 18, width: 58, height: 13, rx: 2, fill: "var(--coal-2)", stroke: "var(--ember-400)" }),
        s("text", { x: 46, y: 27, fill: "var(--ember-200)" }, "Chat.Se…"),
        s("rect", { x: 40, y: 33, width: 58, height: 11, rx: 2, fill: "var(--coal-3)" }),
        s("text", { x: 46, y: 41 }, "Chat.Send"),
        s("rect", { x: 40, y: 44, width: 58, height: 11, rx: 2, fill: "var(--coal-2)" }),
        s("text", { x: 46, y: 52 }, "Chat.Message")),
      s("circle", { cx: 34, cy: 24, r: 3, fill: "var(--ember-300)" }),
      s("text", { x: 6, y: 68, "font-family": "var(--font-mono)", "font-size": "8", fill: "var(--coal-7)" }, "right-click ↑"));
  }
  function glyphWire() {
    return s("svg", { viewBox: "0 0 120 80", class: "pr-glyph" },
      s("rect", { x: 6, y: 22, width: 34, height: 34, rx: 4, fill: "var(--coal-2)", stroke: "var(--coal-5)" }),
      s("rect", { x: 80, y: 30, width: 34, height: 34, rx: 4, fill: "var(--coal-2)", stroke: "var(--coal-5)" }),
      s("circle", { cx: 40, cy: 34, r: 3.6, fill: "#E5A24E" }),
      s("circle", { cx: 80, cy: 44, r: 3.6, fill: "#E5A24E" }),
      s("path", { d: "M40 34 C 62 34, 58 44, 80 44", stroke: "#E5A24E", "stroke-width": "2", fill: "none" }),
      s("circle", { cx: 60, cy: 38.5, r: 2.4, fill: "#E5A24E" }, s("animate", { attributeName: "opacity", values: "0.3;1;0.3", dur: "1.4s", repeatCount: "indefinite" })));
  }
  function glyphSave() {
    return s("svg", { viewBox: "0 0 120 80", class: "pr-glyph" },
      s("rect", { x: 14, y: 16, width: 92, height: 48, rx: 4, fill: "var(--coal-2)", stroke: "var(--coal-5)" }),
      s("circle", { cx: 30, cy: 30, r: 4, fill: "var(--ok)" }),
      s("rect", { x: 42, y: 26, width: 50, height: 6, rx: 2, fill: "var(--coal-5)" }),
      s("rect", { x: 42, y: 40, width: 38, height: 5, rx: 2, fill: "var(--coal-4)" }),
      s("text", { x: 42, y: 58, "font-family": "var(--font-mono)", "font-size": "7.5", fill: "var(--ok)" }, "● live · hot-reloaded"));
  }
  function threeMoves() {
    var moves = [
      { glyph: glyphSpawn(), key: "Space", t: "Spawn", d: 'Press <kbd class="pr-kbd">Space</kbd> &mdash; or right-click the canvas &mdash; to open the spawn palette, then type to filter by name. Pick a trigger, a logic node, an action.' },
      { glyph: glyphWire(), key: "drag", t: "Wire", d: 'Drag from an output pin to a compatible input pin. Colours and shapes must match &mdash; Phoenix refuses a string-into-bool connection.' },
      { glyph: glyphSave(), key: "Ctrl+S", t: "Save", d: 'Save the graph (<code>.phxg</code>) &mdash; Architect exports the generated <code>.phx</code> and Hub hot-reloads it instantly, no restart. Type your command in chat to watch it run.' }
    ];
    return h("div", { class: "pr-moves" }, moves.map(function (m, i) {
      return h("div", { class: "pr-move" },
        h("div", { class: "pr-move-glyph" }, m.glyph),
        h("div", { class: "pr-move-head" }, h("span", { class: "pr-step-num" }, i + 1), h("span", { class: "pr-move-title" }, m.t), h("kbd", { class: "pr-kbd" }, m.key)),
        h("p", { class: "pr-move-d", html: m.d }));
    }));
  }
  function keyStrip() {
    var keys = [["Space", "Spawn node"], ["Ctrl+F", "Find"], ["F", "Frame selection"], ["Ctrl+S", "Save · go live"], ["F1", "Node docs (on a selected node)"]];
    var wrap = h("div", { class: "pr-keys" }, h("span", { class: "h-eyebrow", style: "margin-right:4px" }, "Quick keys"));
    keys.forEach(function (kd) { wrap.appendChild(h("span", { class: "pr-key-item" }, h("kbd", { class: "pr-kbd" }, kd[0]), kd[1])); });
    wrap.appendChild(h("span", { class: "pr-key-item", style: "margin-left:auto;color:var(--coal-7)" }, h("kbd", { class: "pr-kbd" }, "F1"), "opens the full list in-app"));
    return wrap;
  }
  function leftRail() {
    var rows = [
      { dot: "#7FBED1", t: "Variables", hint: "value store", d: 'Named values you read and write across a flow. Scoped <b>Var</b> (graph-local, saved to the databank), <b>Public</b> (run-local, crosses parallel branches), or the databank-backed <b>global</b> store.' },
      { dot: "#9CC97A", t: "Processes", hint: "long-running", d: 'Async loops that run until stopped &mdash; chat collectors, rotating-tip timers, rate limiters. Start and stop them with <code>Process.Start</code> / <code>Process.Stop</code> (often from <code>System.Startup</code>); a running process fires its own triggers (schedules, on_chat, …) until stopped, and reads its parameters as <code>{param.&lt;name&gt;}</code>.' },
      { dot: "#E5A24E", t: "Macros", hint: "reusable subgraphs", d: 'A graph you call like a single node. Build once, reuse everywhere; publish a macro globally to share it across files.' }
    ];
    return h("div", { class: "pr-rail" }, rows.map(function (r) {
      return h("div", { class: "pr-rail-row" },
        h("span", { class: "pr-rail-dot", style: "background:" + r.dot }),
        h("div", { class: "pr-rail-text" },
          h("div", { class: "pr-rail-head" }, h("b", null, r.t), h("span", { class: "pr-rail-hint" }, r.hint)),
          h("p", { html: r.d })));
    }));
  }
  function tokenFamily(fam) {
    return h("div", { class: "pr-fam" },
      h("div", { class: "pr-fam-head" },
        fam.dot ? h("span", { class: "pr-fam-dot", style: "background:" + fam.dot }) : null,
        h("code", { class: "pr-fam-root" }, fam.root + "*"),
        h("span", { class: "pr-fam-label" }, fam.label)),
      h("div", { class: "pr-toks" }, (fam.toks || []).map(function (t) { return h("span", { class: "pr-tok" }, t); })),
      fam.note ? h("p", { class: "pr-fam-note", html: fam.note }) : null);
  }
  function variablesReference(P) {
    var families = P.tokenFamilies || [];
    var stores = P.storageRoots || [];
    var sysToks = P.systemToks || [];
    var streamToks = P.streamToks || [];
    return h("div", null,
      h("p", { class: "pr-section-lede", html:
        'Anywhere a socket takes a value you can type <code>{namespace.key}</code>, and the engine resolves it ' +
        'at run-time in a single left-to-right pass &mdash; <b>local values win over system ones</b>. There are three ' +
        'kinds: <b>runtime tokens</b> an upstream node bound for you, <b>persistent stores</b> you read and write, ' +
        'and the <b>always-on</b> clock catalogue.' }),
      h("div", { class: "pr-sub-eyebrow" }, "Runtime tokens · bound by an upstream node"),
      h("div", { class: "pr-vars" }, families.map(tokenFamily)),
      h("div", { class: "pr-sub-eyebrow" }, "Persistent stores · read & write"),
      h("div", { class: "pr-stores" }, stores.map(function (st) {
        return h("div", { class: "pr-store" }, h("code", { class: "pr-fam-root" }, st.root), h("p", { html: st.d }), h("span", { class: "pr-tok" }, st.ex));
      })),
      h("div", { class: "pr-sub-eyebrow" }, "Always on · no wiring needed"),
      h("div", { class: "pr-cat-on" },
        h("div", { class: "pr-fam" },
          h("div", { class: "pr-fam-head" }, h("code", { class: "pr-fam-root" }, "system.*"), h("span", { class: "pr-fam-label" }, "Clock & calendar — UTC")),
          h("div", { class: "pr-toks" }, sysToks.map(function (t) { return h("span", { class: "pr-tok" }, t); })),
          h("p", { class: "pr-fam-note", html: 'Add a <code>local_</code> prefix &mdash; <code>{system.local_time}</code> &mdash; for the streamer’s wall-clock instead of UTC.' })),
        h("div", { class: "pr-fam" },
          h("div", { class: "pr-fam-head" }, h("code", { class: "pr-fam-root" }, "stream.*"), h("span", { class: "pr-fam-label" }, "Uptime since stream start")),
          h("div", { class: "pr-toks" }, streamToks.map(function (t) { return h("span", { class: "pr-tok" }, t); })),
          h("p", { class: "pr-fam-note", html: '<code>{stream.uptime}</code> reads like <em>“1h 23m”</em>. Anchored to the stream’s real start time, refreshed from Twitch about once a minute; falls back to Hub start when that lookup isn’t available.' }))));
  }
  function keyboardReference(groups) {
    return h("div", { class: "pr-keys-grid" }, (groups || []).map(function (g) {
      return h("div", { class: "pr-key-group" },
        h("div", { class: "pr-key-group-head" }, h("span", { class: "pr-key-sec" }, g.sec), g.note ? h("span", { class: "pr-key-note" }, g.note) : null),
        (g.rows || []).map(function (r) {
          return h("div", { class: "pr-key-row" }, h("kbd", { class: "pr-kbd" }, r[0]), h("span", null, r[1]));
        }));
    }));
  }
  function primerBlock(id, eyebrow, title, body) {
    return h("section", { class: "pr-block", id: id },
      h("div", { class: "pr-block-head" }, h("span", { class: "h-eyebrow" }, eyebrow), h("h2", { class: "pr-h2" }, title)),
      body);
  }
  function buildPrimer() {
    var P = PHX.PRIMER || {};
    return h("div", { class: "primer", id: "primer" },
      h("div", { class: "pr-intro" },
        h("span", { class: "h-eyebrow" }, "Start here"),
        h("h2", { class: "pr-h1", html: "Using the <em>Architect</em>" }),
        h("p", { class: "pr-lede", html:
          'The Architect is where you author everything that happens off-screen &mdash; a node graph that turns ' +
          'stream events into actions. Drag nodes onto the canvas, wire them together, and save. Hub picks the ' +
          'file up immediately and runs it live. Nothing fires, though, until Hub is running with Streamer.bot ' +
          'connected and the Phoenix action pack imported. Below: how a flow is shaped, how to read a node, and the three ' +
          'moves you’ll repeat for every flow you ever build. Then the full node catalog follows.' })),
      primerBlock("primer-flow", "The shape of every flow", "Anatomy of a flow", flowDiagram()),
      primerBlock("primer-node", "Read it at a glance", "Anatomy of a node", nodeAnatomy()),
      primerBlock("primer-build", "The loop you'll repeat", "Three moves to build a flow", [threeMoves(), keyStrip()]),
      primerBlock("primer-rail", "The left rail", "Variables, Processes & Macros", leftRail()),
      primerBlock("primer-vars", "Drop a {…} into any value", "Variables & tokens", variablesReference(P)),
      primerBlock("primer-keys", "Every chord on the canvas", "Keyboard reference", keyboardReference(P.hotkeyGroups)),
      h("div", { class: "pr-handoff" }, h("span", { class: "pr-handoff-line" }), h("span", null, "Now — every node you can spawn"), h("span", { class: "pr-handoff-line" })));
  }

  // ════════════════════════════════════════════════════════════════
  //  NODE CATALOG
  // ════════════════════════════════════════════════════════════════
  function docNode(node, group) {
    var ins = node.ins || [], outs = node.outs || [];
    var rows = Math.max(ins.length, outs.length, 1), rowEls = [];
    for (var i = 0; i < rows; i++) {
      var inP = ins[i], outP = outs[i];
      var left = h("div", { style: "display:flex;align-items:center;gap:6px;position:relative" });
      if (inP) {
        left.appendChild(h("span", { style: "position:absolute;left:-10px;top:50%;transform:translateY(-50%)" }, pin(inP.k, inP.opt, 14)));
        left.appendChild(h("span", { style: "margin-left:8px;font-size:11px;color:var(--coal-8)" }, inP.n || h("em", { style: "color:var(--coal-6);font-style:normal" }, "exec")));
        if (inP.v) left.appendChild(h("span", { style: "margin-left:auto;font-family:var(--font-mono);font-size:10px;background:var(--coal-0);border:1px solid var(--coal-4);border-radius:2px;padding:1px 5px;color:var(--ember-200);max-width:100px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" }, inP.v));
      }
      var right = h("div", { style: "display:flex;align-items:center;gap:6px;justify-content:flex-end;position:relative" });
      if (outP) {
        right.appendChild(h("span", { style: "font-size:11px;color:var(--coal-8);margin-right:8px" }, outP.n || h("em", { style: "color:var(--coal-6);font-style:normal" }, "exec")));
        right.appendChild(h("span", { style: "position:absolute;right:-10px;top:50%;transform:translateY(-50%)" }, pin(outP.k, false, 14)));
      }
      rowEls.push(h("div", { style: "display:grid;grid-template-columns:1fr 1fr;padding:0 8px;gap:8px;align-items:center;min-height:22px" }, left, right));
    }
    return h("div", { style: "width:240px;background:var(--coal-2);border:1px solid var(--coal-5);border-radius:6px;box-shadow:var(--sh-card);font-family:var(--font-sans);user-select:none;position:relative" },
      h("div", { style: "padding:5px 10px;background:linear-gradient(180deg," + group.color + "," + group.color + "AA);border-top-left-radius:5px;border-top-right-radius:5px;border-bottom:1px solid rgba(0,0,0,0.4);display:flex;align-items:center;gap:6px" },
        h("span", { style: "font-size:9px;letter-spacing:0.12em;text-transform:uppercase;color:rgba(255,255,255,0.78);font-weight:700" }, group.cat)),
      h("div", { style: "padding:8px 12px 8px;border-bottom:1px solid var(--coal-3);color:var(--coal-10);font-size:13.5px;font-weight:600;letter-spacing:-0.01em" }, node.name),
      h("div", { style: "padding:8px 0 10px;display:flex;flex-direction:column;gap:4px" }, rowEls));
  }
  function renderPort(p) {
    var fields = p.fields || (p.k === "user" ? USER_FIELDS : null);
    var frag = document.createDocumentFragment();
    frag.appendChild(h("div", { class: "port" },
      h("span", { class: "glyph" }, pin(p.k, p.opt, 14)),
      h("span", { class: "nm" }, p.n || h("em", null, "exec")),
      h("span", { class: "ty" }, ":" + p.k + (p.opt ? "?" : "")),
      p.v ? h("span", { class: "def" }, p.v) : null));
    if (fields) {
      frag.appendChild(h("ul", { class: "port-fields" }, fields.map(function (f) {
        return h("li", null, h("span", { class: "f-nm" }, "." + f.n), h("span", { class: "f-ty" }, ":" + f.k), h("span", { class: "f-d" }, f.d));
      })));
    }
    return frag;
  }
  function portsTable(ins, outs) {
    ins = ins || []; outs = outs || [];
    if (!ins.length && !outs.length) return null;
    var inCol = h("div", null, h("div", { class: "col-hdr" }, "Inputs"));
    if (!ins.length) inCol.appendChild(h("div", { class: "port" }, h("span", { class: "ty" }, "— none (event source) —")));
    ins.forEach(function (p) { inCol.appendChild(renderPort(p)); });
    var outCol = h("div", null, h("div", { class: "col-hdr" }, "Outputs"));
    if (!outs.length) outCol.appendChild(h("div", { class: "port" }, h("span", { class: "ty" }, "— none —")));
    outs.forEach(function (p) { outCol.appendChild(renderPort(p)); });
    return h("div", { class: "ports" }, inCol, outCol);
  }
  function nodeCard(node, group) {
    var nslug = "n-" + slug(node.name);
    var parts = node.name.split(".");
    var head = h("div", { class: "node-head" },
      h("h3", null, h("span", { class: "ns" }, parts[0] + "."), parts.slice(1).join(".")),
      (node.badge || group.badge) ? h("span", { class: "new-badge" }, node.badge || "NEW") : null,
      node.since ? h("span", { class: "since" }, "since " + node.since) : null);
    var body = h("div", { class: "node-body" }, head,
      h("p", { class: "node-summary", html: node.summary || "" }),
      portsTable(node.ins, node.outs),
      node.description ? h("div", { class: "node-desc", html: node.description }) : null,
      node.example ? h("div", { class: "example" }, h("div", { class: "ex-lbl" }, "Typical use"), h("span", { html: node.example })) : null);
    var card = h("article", { class: "node-card", id: nslug }, h("div", { class: "node-stage", style: "--cat-tint:" + (group.tint || "var(--coal-0)") }, docNode(node, group)), body);
    // searchable text cache for the filter
    card._search = (node.name + " " + (node.summary || "") + " " + group.cat).toLowerCase();
    return card;
  }
  function categorySection(group) {
    var cslug = "c-" + slug(group.cat);
    var sec = h("section", { class: "cat", id: cslug },
      h("div", { class: "cat-head" },
        h("span", { class: "swatch", style: "background:" + group.color }),
        h("h2", null, group.cat),
        group.badge ? h("span", { class: "new-badge" }, group.badge) : null),
      group.blurb ? h("p", { class: "cat-lede", html: group.blurb }) : null);
    var lastSub = null;
    (group.nodes || []).forEach(function (n) {
      if (n.sub && n.sub !== lastSub) {
        lastSub = n.sub;
        sec.appendChild(h("div", { class: "subcat", id: cslug + "-" + slug(n.sub) }, n.sub));
      }
      sec.appendChild(nodeCard(n, group));
    });
    return sec;
  }

  // ── TOC ───────────────────────────────────────────────────────────
  function buildToc(catalog) {
    var frag = document.createDocumentFragment();
    frag.appendChild(h("div", { class: "toc-title" }, "Getting started"));
    [["#primer", "Using the Architect"], ["#primer-flow", "Anatomy of a flow"], ["#primer-node", "Anatomy of a node"],
     ["#primer-build", "Three moves"], ["#primer-rail", "Variables · Processes · Macros"], ["#primer-vars", "Variables & tokens"],
     ["#primer-keys", "Keyboard reference"]].forEach(function (l) {
      frag.appendChild(h("a", { href: l[0], class: "toc-primer" }, h("span", null, l[1])));
    });
    frag.appendChild(h("div", { class: "toc-title", style: "margin-top:18px" }, "Categories"));
    catalog.forEach(function (group) {
      frag.appendChild(h("a", { href: "#c-" + slug(group.cat), dataset: { cat: slug(group.cat) } },
        h("span", { class: "sw", style: "background:" + group.color }),
        h("span", null, group.cat),
        h("span", { class: "ct" }, (group.nodes || []).length)));
    });
    return frag;
  }

  // ── socket legend ─────────────────────────────────────────────────
  var LEGEND_LABELS = {
    flow: ["flow", "execution edge"], string: ["string", "text"], number: ["number", "int / float"],
    bool: ["bool", "true / false"], user: ["user", "chatter identity"], object: ["object", "JSON / blob"]
  };
  function buildLegend() {
    var row = document.getElementById("legend-row");
    Object.keys(SOCKETS).forEach(function (kind) {
      var lbl = LEGEND_LABELS[kind] || [kind, ""];
      row.appendChild(h("div", { class: "legend-item" }, pin(kind, false, 14), h("b", null, lbl[0]), h("span", { class: "sub" }, lbl[1])));
    });
    row.appendChild(h("div", { class: "legend-item" }, h("span", { class: "sub" }, "Hollow shape = optional input · solid = required")));
  }

  // ════════════════════════════════════════════════════════════════
  //  MOUNT
  // ════════════════════════════════════════════════════════════════
  var catalog = PHX.NODE_CATALOG;
  var META = PHX.META || {};
  document.getElementById("meta-cats").textContent = META.categories != null ? META.categories : catalog.length;
  document.getElementById("meta-nodes").textContent = META.nodes != null ? META.nodes : catalog.reduce(function (a, g) { return a + (g.nodes || []).length; }, 0);
  document.getElementById("meta-version").textContent = META.version || "current";

  buildLegend();
  var root = document.getElementById("nodes-root");
  root.appendChild(buildPrimer());
  catalog.forEach(function (g) { root.appendChild(categorySection(g)); });
  document.getElementById("toc").appendChild(buildToc(catalog));
  var noMatches = h("div", { class: "no-matches is-hidden" }, "No nodes match ", h("b", { id: "nm-q" }, ""), ".");
  root.appendChild(noMatches);

  // ── deep-link (F1-from-node) ──────────────────────────────────────
  function jumpTo(anchor) {
    if (!anchor) return;
    var el = document.getElementById(anchor);
    if (el) { el.scrollIntoView({ behavior: "smooth", block: "start" }); flash(el); }
  }
  function flash(el) {
    el.style.transition = "box-shadow .25s ease";
    var prev = el.style.boxShadow;
    el.style.boxShadow = "var(--sh-glow)";
    setTimeout(function () { el.style.boxShadow = prev; }, 1100);
  }
  if (PHX.INITIAL_ANCHOR) setTimeout(function () { jumpTo(PHX.INITIAL_ANCHOR); }, 60);
  // host can re-navigate an already-open window:
  window.phxNavigate = function (anchor) { jumpTo(anchor); };

  // ── filter ────────────────────────────────────────────────────────
  var filterBox = document.getElementById("filter");
  var filterClear = document.getElementById("filter-clear");
  var allCards = Array.prototype.slice.call(root.querySelectorAll(".node-card"));
  var allCats = Array.prototype.slice.call(root.querySelectorAll(".cat"));
  function applyFilter(q) {
    q = (q || "").trim().toLowerCase();
    filterClear.hidden = q.length === 0;
    var anyVisible = false;
    if (!q) {
      allCards.forEach(function (c) { c.classList.remove("is-hidden"); });
      allCats.forEach(function (c) { c.classList.remove("is-hidden"); });
      root.querySelectorAll(".subcat").forEach(function (s2) { s2.classList.remove("is-hidden"); });
      noMatches.classList.add("is-hidden");
      return;
    }
    allCats.forEach(function (cat) {
      var cards = cat.querySelectorAll(".node-card");
      var catHas = false;
      cards.forEach(function (c) {
        var match = c._search.indexOf(q) !== -1;
        c.classList.toggle("is-hidden", !match);
        if (match) { catHas = true; anyVisible = true; }
      });
      cat.classList.toggle("is-hidden", !catHas);
    });
    root.querySelectorAll(".subcat").forEach(function (s2) { s2.classList.add("is-hidden"); });
    document.getElementById("nm-q").textContent = q;
    noMatches.classList.toggle("is-hidden", anyVisible);
  }
  filterBox.addEventListener("input", function () { applyFilter(filterBox.value); });
  filterClear.addEventListener("click", function () { filterBox.value = ""; applyFilter(""); filterBox.focus(); });
  document.addEventListener("keydown", function (e) {
    if (e.key === "/" && document.activeElement !== filterBox) { e.preventDefault(); filterBox.focus(); }
    else if (e.key === "Escape" && document.activeElement === filterBox) { filterBox.value = ""; applyFilter(""); filterBox.blur(); }
  });

  // ── scroll-spy active TOC ─────────────────────────────────────────
  var tocLinks = {};
  document.querySelectorAll(".toc a[data-cat]").forEach(function (a) { tocLinks[a.dataset.cat] = a; });
  if ("IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (!en.isIntersecting) return;
        var id = en.target.id.replace(/^c-/, "");
        Object.keys(tocLinks).forEach(function (k) { tocLinks[k].classList.toggle("active", k === id); });
      });
    }, { rootMargin: "-72px 0px -70% 0px", threshold: 0 });
    allCats.forEach(function (c) { io.observe(c); });
  }
})();
