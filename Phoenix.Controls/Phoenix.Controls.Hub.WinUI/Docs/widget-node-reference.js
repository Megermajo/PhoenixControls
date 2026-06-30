/* Phoenix Controls — Visualist Widget Node Reference: renderer (dependency-free)
 *
 * The Visualist mirror of node-reference.js. Same offline, framework-free DOM
 * builder and the same doc-viewer.css, but with a Visualist-flavoured primer
 * (the compositor model: layer → widget → trigger → graph → Display) and the
 * visual socket type system (Image / Color / Scalar / Vector …).
 *
 * Data contract — Hub injects `window.PHX` BEFORE this script runs, via
 * CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync, built by
 * WidgetNodeReferenceData from the live WidgetNodeRegistry + authored prose:
 *
 *   window.PHX = {
 *     META:    { categories, nodes, version, pillar },
 *     SOCKETS: { <kind>: { color, shape } },          // shape: chevron|circle|triangle|diamond|square
 *     USER_FIELDS: [],                                 // unused on the visual side
 *     NODE_CATALOG: [ { cat, color, tint, blurb, badge?, nodes: [
 *         { name, summary, ins:[{k,n,v?}], outs:[...], description?, example?, since?, badge? }
 *     ] } ],
 *     INITIAL_ANCHOR: "n-image-load" | null            // deep-link target for F1-from-node
 *   }
 *
 * The primer below is static (no PHX.PRIMER) — its content is stable Visualist
 * guidance, not registry-derived.
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
    // a raw string. Mirror h()'s text handling so <text> labels work.
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
      'Open this page from <b>Visualist → Help → Widget Node Reference</b> (or press <b>F1</b> on a node).</div>';
    return;
  }
  var SOCKETS = PHX.SOCKETS || {};

  // ── socket glyph (shared shape language with the editor) ───────────
  function pin(kind, opt, size) {
    var def = SOCKETS[kind] || SOCKETS.any || { color: "#A89683", shape: "square" };
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
  function inlinePins(root) {
    root.querySelectorAll("[data-pin]").forEach(function (span) { span.appendChild(pin(span.dataset.pin, false, 14)); });
  }

  // ════════════════════════════════════════════════════════════════
  //  PRIMER — "Using Visualist"
  // ════════════════════════════════════════════════════════════════
  function chip(label, sub, tone) {
    return h("div", { style: "flex:0 0 auto;background:var(--coal-2);border:1px solid " + (tone || "var(--coal-5)") + ";border-radius:6px;box-shadow:var(--sh-card);padding:10px 14px;min-width:110px;text-align:center" },
      h("div", { style: "font-family:var(--font-display);font-size:17px;color:var(--coal-10);letter-spacing:0.02em" }, label),
      sub ? h("div", { style: "font-family:var(--font-mono);font-size:10px;color:var(--coal-7);margin-top:2px" }, sub) : null);
  }
  function arrow() {
    return h("div", { style: "flex:0 0 auto;color:var(--ember-300);font-size:18px;align-self:center;padding:0 2px" }, "→");
  }
  function pipeStrip(steps) {
    var row = h("div", { style: "display:flex;flex-wrap:wrap;gap:8px;align-items:stretch;padding:18px 16px" });
    steps.forEach(function (st, i) {
      if (i) row.appendChild(arrow());
      row.appendChild(chip(st[0], st[1], st[2]));
    });
    return row;
  }

  function layerAnatomy() {
    var stage = h("div", { class: "pr-stage" },
      pipeStrip([
        ["Layer", ".phxlayer", "var(--ember-400)"],
        ["Widget", "a region", null],
        ["Trigger", "onStartup / onTrigger", null],
        ["Graph", "nodes", null],
        ["Display", "the sink", "var(--ember-400)"]
      ]));
    var cap = h("p", { class: "pr-cap", html:
      'A <b>.phxlayer</b> is one OBS browser source. It holds <b>widgets</b> (rectangular regions); each widget has ' +
      '<b>triggers</b> &mdash; <code>onStartup</code> for its resting state and <code>onTrigger:&lt;name&gt;</code> for ' +
      'reactions. Every trigger owns a <b>node graph</b> and a <b>timeline</b>. The graph ends at an auto-injected ' +
      '<b>Display</b> sink, and the compositor evaluates by walking <em>upstream</em> from it &mdash; so only nodes that ' +
      'actually feed Display do any work.' });
    stage.appendChild(cap);
    return stage;
  }

  function nodeAnatomy() {
    var rows = [
      ["1", "<b>Typed pins</b> &mdash; a socket's <em>shape and colour</em> are its data type. A blue square is an <code>image</code>, a green circle a <code>scalar</code> number, an amber diamond a <code>vector</code>. You can only wire matching types together."],
      ["2", "<b>Inline values</b> &mdash; an unwired input shows its default on the node body. Edit it there, or wire a socket to drive it instead (a wired value always wins)."],
      ["3", "<b>Pull, don't push</b> &mdash; data flows from <em>sources</em> on the left (loaders, constants) rightward through kernels into <b>Display</b>. The graph is pulled from Display, not run top-to-bottom."],
      ["4", "<b>Live thumbnails</b> &mdash; image nodes paint a preview of the pixels passing through them, so you see the composite take shape as you wire (drop a <code>Viewer</code> anywhere to inspect a wire)."]
    ].map(function (r) { return h("li", null, h("span", { class: "pr-k-num" }, r[0]), h("div", { html: r[1] })); });
    return h("div", { class: "pr-card", style: "padding:6px 4px" }, h("ol", { class: "pr-anatomy-key" }, rows));
  }

  var TYPE_NOTES = {
    image:   "Pixels — the thing that ends up on screen. Output of loaders, masks, text-render and every Image.* kernel.",
    color:   "A single colour (hex). Feeds gradients, particle tint and text fill / stroke.",
    scalar:  "A number, 0–1 by convention. The animatable value — drive it from Time.* or a keyframe.",
    float:   "A raw number (pixels, seconds) that isn't normalised — e.g. Text.Render's font size.",
    vector2: "Two numbers — a position, scale or size (x, y).",
    vector3: "Three numbers — a colour-as-RGB or a 3D value (x, y, z).",
    vector4: "Four numbers — an RGBA colour or a rectangle (x, y, z, w).",
    string:  "Text — from a trigger field or a String node, into Text.Render.",
    audio:   "A sound source for the Audio.Play sink.",
    flow:    "A lifecycle edge on the trigger nodes — carries no data, only sequencing.",
    any:     "A wildcard that accepts any type (the Viewer tap)."
  };
  function typesReference() {
    var order = ["image", "color", "scalar", "float", "vector2", "vector3", "vector4", "string", "audio", "flow", "any"];
    var grid = h("div", { class: "pr-vars" });
    order.forEach(function (kind) {
      var def = SOCKETS[kind];
      if (!def) return;
      grid.appendChild(h("div", { class: "pr-fam" },
        h("div", { class: "pr-fam-head" },
          h("span", { style: "display:inline-flex" }, pin(kind, false, 14)),
          h("code", { class: "pr-fam-root" }, kind),
          h("span", { class: "pr-fam-label" }, def.shape)),
        h("p", { class: "pr-fam-note", html: TYPE_NOTES[kind] || "" })));
    });
    return h("div", null,
      h("p", { class: "pr-section-lede", html:
        'Visualist sockets are <b>typed</b> — the pin tells you what flows through it, and the editor only lets you wire ' +
        'compatible types. Build values up (scalars into a <code>vector</code>, channels into a <code>color</code>) and ' +
        'feed the result into the image chain.' }),
      grid);
  }

  function pipelineSection() {
    var stage = h("div", { class: "pr-stage" },
      pipeStrip([
        ["Load", "source", "var(--ember-400)"],
        ["Crop", null, null],
        ["ColorAdjust", "grade", null],
        ["Transform", "move", null],
        ["Effects", "blur/glow…", null],
        ["Mask", null, null],
        ["Blend", "compose", null],
        ["Display", null, "var(--ember-400)"]
      ]));
    var cap = h("p", { class: "pr-cap", html:
      'Image nodes chain in a canonical order. You rarely need all of them &mdash; a typical alert is just ' +
      '<code>Image.Load &rarr; Image.Scale &rarr; Display</code>. Procedural <code>Mask.*</code> shapes and ' +
      '<code>Text.Render</code> are also image sources, so you can build a composite from nothing but nodes.' });
    stage.appendChild(cap);
    return stage;
  }

  function triggersRail() {
    var rows = [
      { dot: "#7A2E2E", t: "Visual.OnStartup", hint: "resting state", d: 'Fires when the layer loads. Wire whatever the widget shows when nothing is happening.' },
      { dot: "#7A2E2E", t: "Visual.OnTrigger", hint: "reacts", d: 'Fires on an Architect <code>Visual.Trigger</code>. Exposes <code>TriggerName</code>, <code>UserName</code>, <code>Message</code> and raw <code>EventData</code>.' },
      { dot: "#7A2E2E", t: "Result.If", hint: "branch", d: 'Passes an image through only when an event field matches &mdash; e.g. <code>When=Args1, Equals=win</code>. Wire two for two outcomes.' },
      { dot: "#7A2E2E", t: "Visual.Complete", hint: "done", d: 'Tells Hub the effect finished, so an Architect <code>Async.WaitForVisual</code> continues on its <code>Done</code> branch.' }
    ];
    return h("div", { class: "pr-rail" }, rows.map(function (r) {
      return h("div", { class: "pr-rail-row" },
        h("span", { class: "pr-rail-dot", style: "background:" + r.dot }),
        h("div", { class: "pr-rail-text" },
          h("div", { class: "pr-rail-head" }, h("b", null, r.t), h("span", { class: "pr-rail-hint" }, r.hint)),
          h("p", { html: r.d })));
    }));
  }

  function keyframesSection() {
    return h("div", null,
      h("p", { class: "pr-section-lede", html:
        'Anything wired to a <code>scalar</code> can be <b>animated</b> two ways. Drop a keyframe on a node attribute in ' +
        'the timeline and Visualist interpolates it over time with an easing curve; or drive the value from a ' +
        '<code>Time.*</code> node for a continuous, self-running motion.' }),
      h("div", { class: "pr-cat-on" },
        h("div", { class: "pr-fam" },
          h("div", { class: "pr-fam-head" }, h("code", { class: "pr-fam-root" }, "Timeline keyframes"), h("span", { class: "pr-fam-label" }, "authored")),
          h("p", { class: "pr-fam-note", html: 'Curves: <code>Linear</code>, <code>EaseIn</code>, <code>EaseOut</code>, <code>EaseInOut</code>, <code>Bezier</code>, <code>Step</code>. Drag handles in the curve editor for custom Béziers. Values interpolate between keyframes as the trigger plays.' })),
        h("div", { class: "pr-fam" },
          h("div", { class: "pr-fam-head" }, h("code", { class: "pr-fam-root" }, "Time.* nodes"), h("span", { class: "pr-fam-label" }, "continuous")),
          h("p", { class: "pr-fam-note", html: '<code>Time.Elapsed</code> is the clock; <code>Time.Oscillator</code> / <code>Time.Sawtooth</code> loop; <code>Time.Easing</code> shapes a 0–1 input. Feed them through <code>Math.*</code> to drive position, scale, opacity — anything.' }))));
  }

  function primerBlock(id, eyebrow, title, body) {
    return h("section", { class: "pr-block", id: id },
      h("div", { class: "pr-block-head" }, h("span", { class: "h-eyebrow" }, eyebrow), h("h2", { class: "pr-h2" }, title)),
      body);
  }
  function buildPrimer() {
    var root = h("div", { class: "primer", id: "primer" },
      h("div", { class: "pr-intro" },
        h("span", { class: "h-eyebrow" }, "Start here"),
        h("h2", { class: "pr-h1", html: "Using <em>Visualist</em>" }),
        h("p", { class: "pr-lede", html:
          'Visualist is the compositor — a pull-based node graph that turns sources into the pixels OBS shows. You ' +
          'build a widget by wiring loaders, shapes and effects into a <b>Display</b> sink, then animate it with ' +
          'keyframes or time nodes. Below: how a layer is shaped, how to read a typed node, the image pipeline, and how ' +
          'triggers from Architect reach the graph. Then the full node catalog follows.' })),
      primerBlock("primer-layer", "The shape of a widget", "Anatomy of a layer", layerAnatomy()),
      primerBlock("primer-node", "Read it at a glance", "Anatomy of a node", nodeAnatomy()),
      primerBlock("primer-types", "What flows through a wire", "The socket types", typesReference()),
      primerBlock("primer-pipeline", "Source to screen", "The image pipeline", pipelineSection()),
      primerBlock("primer-triggers", "Reacting to logic", "Triggers & event data", triggersRail()),
      primerBlock("primer-keys", "Make it move", "Keyframes & animation", keyframesSection()),
      h("div", { class: "pr-handoff" }, h("span", { class: "pr-handoff-line" }), h("span", null, "Now — every node you can spawn"), h("span", { class: "pr-handoff-line" })));
    inlinePins(root);
    return root;
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
    return h("div", { class: "port" },
      h("span", { class: "glyph" }, pin(p.k, p.opt, 14)),
      h("span", { class: "nm" }, p.n || h("em", null, "exec")),
      h("span", { class: "ty" }, ":" + p.k + (p.opt ? "?" : "")),
      p.v ? h("span", { class: "def" }, p.v) : null);
  }
  function portsTable(ins, outs) {
    ins = ins || []; outs = outs || [];
    if (!ins.length && !outs.length) return null;
    var inCol = h("div", null, h("div", { class: "col-hdr" }, "Inputs"));
    if (!ins.length) inCol.appendChild(h("div", { class: "port" }, h("span", { class: "ty" }, "— none (source) —")));
    ins.forEach(function (p) { inCol.appendChild(renderPort(p)); });
    var outCol = h("div", null, h("div", { class: "col-hdr" }, "Outputs"));
    if (!outs.length) outCol.appendChild(h("div", { class: "port" }, h("span", { class: "ty" }, "— none (sink) —")));
    outs.forEach(function (p) { outCol.appendChild(renderPort(p)); });
    return h("div", { class: "ports" }, inCol, outCol);
  }
  function nodeCard(node, group) {
    var nslug = "n-" + slug(node.name);
    var parts = node.name.split(".");
    var head = h("div", { class: "node-head" },
      h("h3", null, h("span", { class: "ns" }, parts[0] + (parts.length > 1 ? "." : "")), parts.slice(1).join(".")),
      (node.badge || group.badge) ? h("span", { class: "new-badge" }, node.badge || "NEW") : null,
      node.since ? h("span", { class: "since" }, "since " + node.since) : null);
    var body = h("div", { class: "node-body" }, head,
      h("p", { class: "node-summary", html: node.summary || "" }),
      portsTable(node.ins, node.outs),
      node.description ? h("div", { class: "node-desc", html: node.description }) : null,
      node.example ? h("div", { class: "example" }, h("div", { class: "ex-lbl" }, "Typical use"), h("span", { html: node.example })) : null);
    var card = h("article", { class: "node-card", id: nslug }, h("div", { class: "node-stage", style: "--cat-tint:" + (group.tint || "var(--coal-0)") }, docNode(node, group)), body);
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
    (group.nodes || []).forEach(function (n) { sec.appendChild(nodeCard(n, group)); });
    return sec;
  }

  // ── TOC ───────────────────────────────────────────────────────────
  function buildToc(catalog) {
    var frag = document.createDocumentFragment();
    frag.appendChild(h("div", { class: "toc-title" }, "Getting started"));
    [["#primer", "Using Visualist"], ["#primer-layer", "Anatomy of a layer"], ["#primer-node", "Anatomy of a node"],
     ["#primer-types", "The socket types"], ["#primer-pipeline", "The image pipeline"], ["#primer-triggers", "Triggers & event data"],
     ["#primer-keys", "Keyframes & animation"]].forEach(function (l) {
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
    flow: ["flow", "lifecycle edge"], image: ["image", "pixels"], color: ["color", "a colour"],
    scalar: ["scalar", "number · 0–1"], float: ["float", "raw number"],
    vector2: ["vector2", "x, y"], vector3: ["vector3", "x, y, z"], vector4: ["vector4", "x, y, z, w"],
    string: ["string", "text"], audio: ["audio", "sound"], any: ["any", "wildcard"]
  };
  function buildLegend() {
    var row = document.getElementById("legend-row");
    Object.keys(SOCKETS).forEach(function (kind) {
      var lbl = LEGEND_LABELS[kind] || [kind, ""];
      row.appendChild(h("div", { class: "legend-item" }, pin(kind, false, 14), h("b", null, lbl[0]), h("span", { class: "sub" }, lbl[1])));
    });
    row.appendChild(h("div", { class: "legend-item" }, h("span", { class: "sub" }, "Shape & colour = the socket's data type")));
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
