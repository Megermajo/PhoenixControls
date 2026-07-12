// Connected dashboard. Calls /v/api/snapshot once for bootstrap, then
// opens a WS to /v/ws and patches the four panels in place. Pure read —
// every interactive control is rendered disabled.

import { apiBase } from "./app.js";
import { topbarMarkup, el, escapeHtml } from "./pair.js";

const MAX_FEED = 200;
const MAX_CHAT = 200;
const MAX_LOG  = 500;

// WS reconnect policy — mirrors compositor.js (1.5× backoff, capped at 30s).
// The flat 2s retry that lived here before flooded a recovering Hub or a
// revoked-device endpoint with one connect attempt every 2s forever.
const WS_RECONNECT_MIN_MS = 1500;
const WS_RECONNECT_MAX_MS = 30000;
const WS_RECONNECT_FACTOR = 1.5;

// QC27-06 — terminal application-level close codes from ViewerServer.
//   4401 — bearer revoked / unknown post-handshake
//   4403 — forbidden (reserved; not currently emitted)
//   4404 — channel / route gone (reserved)
// Hitting any of these means re-pairing is required; further reconnect
// attempts would loop forever against a permanently-401 endpoint.
const WS_TERMINAL_CLOSE_CODES = new Set([4401, 4403, 4404]);

// QC27-06 — fallback for the handshake-401 case: a browser WebSocket
// failing the initial HTTP upgrade surfaces as close code 1006 (abnormal)
// with wasClean=false and zero on-open events ever fired. We can't
// distinguish "Hub momentarily down" from "token rejected" on a single
// failure, so we count consecutive failed opens; after this many in a row
// we assume the token's gone bad and stop, surfacing the re-pair message.
const WS_MAX_OPENLESS_RETRIES = 5;

// Latency + staleness cadence. The PING round-trips through ViewerServer
// as VIEWER_PONG echoing our own Date.now(), so the topbar RTT is measured
// entirely on this device's clock. The 1s ticker repaints the footer's
// "last sync" from the local receive clock — never from envelope.sentAt,
// whose server clock may be skewed against ours.
const PING_INTERVAL_MS = 10000;
const SYNC_TICK_MS     = 1000;

// Interval handles — module-level singletons because only one connected
// dashboard exists per page. renderConnected wipes root.innerHTML on
// re-render, so clearing these before (re)creating them guarantees an
// orphaned ticker can never keep painting into a detached footer node.
let pingTimer  = null;
let syncTimer  = null;
let timerOwner = null; // the WebSocket the live intervals belong to

// Connection generation — bumped whenever the dashboard (re)renders, the
// user disconnects, or auth terminates. Every socket chain (openWs → its
// open/message/close handlers → scheduleReconnect) captures the generation
// it was born under and no-ops once it's stale, so an abandoned socket's
// reconnect loop can never re-open later and steal the live dashboard's
// timers or repaint its DOM. The stopTimers owner-guard below stays as
// belt-and-braces beneath this.
let connectionGen = 0;
let activeWs = null; // socket owned by the current generation (for teardown)

// When an owner is given, only that socket's own intervals are cleared —
// a stale socket's late "close" event must not kill the timers a newer
// connection just started.
function stopTimers(owner) {
  if (owner && owner !== timerOwner) return;
  if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
  if (syncTimer) { clearInterval(syncTimer); syncTimer = null; }
  timerOwner = null;
}

function startTimers(ws, state, ui) {
  stopTimers();
  timerOwner = ws;
  const sendPing = () => {
    if (ws.readyState !== WebSocket.OPEN) return;
    try { ws.send(JSON.stringify({ type: "PING", t: Date.now() })); }
    catch { /* socket died mid-send — the close handler owns recovery */ }
  };
  sendPing(); // fire once on open so the latency pill fills promptly
  pingTimer = setInterval(sendPing, PING_INTERVAL_MS);
  syncTimer = setInterval(() => {
    if (!state.lastRecvMs) return;
    const age = (Date.now() - state.lastRecvMs) / 1000;
    ui.footSync.textContent = `last sync ${age.toFixed(1)}s ago`;
  }, SYNC_TICK_MS);
}

export function renderConnected(root, ctx) {
  // Retire any previous connection chain before repainting — its socket,
  // handlers, and pending reconnects all see a stale generation and no-op.
  const gen = ++connectionGen;
  stopTimers();
  root.innerHTML = "";
  root.appendChild(el("div", "viewer-watermark"));
  const topbar = topbarMarkup(ctx.channel, "lan", null);
  root.appendChild(topbar);

  const strip = el("div", "viewer-statusstrip");
  root.appendChild(strip);

  const grid = el("div", "viewer-grid");
  const feedPane    = el("div", "panel pane-livefeed");
  const syslogPane  = el("div", "panel pane-syslog");
  const chatPane    = el("div", "panel pane-chat");
  const scriptsPane = el("div", "panel pane-scripts");
  grid.appendChild(feedPane);
  grid.appendChild(syslogPane);
  grid.appendChild(chatPane);
  grid.appendChild(scriptsPane);
  root.appendChild(grid);

  const footer = el("div", "viewer-footer");
  const footMain = el("span");
  footMain.textContent = `paired · device "${ctx.getCreds()?.label ?? "device"}" · just now`;
  footer.appendChild(footMain);
  footer.appendChild(document.createTextNode(" · "));
  const footSync = el("span");
  footSync.textContent = "last sync —";
  footer.appendChild(footSync);
  footer.appendChild(el("div", "spacer"));
  const disc = el("button", "link");
  disc.textContent = "Disconnect";
  // onDisconnect re-renders the pair screen; the abandoned socket and its
  // reconnect loop must die with it. Bump the generation FIRST so the
  // socket's own close event (and any queued reconnect) sees itself as
  // stale, then close the socket so it can't linger half-open.
  disc.addEventListener("click", () => {
    connectionGen++;
    stopTimers();
    try { activeWs?.close(); } catch { /* already dead */ }
    activeWs = null;
    ctx.onDisconnect();
  });
  footer.appendChild(disc);
  root.appendChild(footer);

  // Mutable state — every render reads from this.
  const state = {
    feed: [],
    chat: [],
    scripts: [],
    syslog: [],
    status: { streamerBot: "Disconnected", hudOverlay: "Disconnected", ipcBus: "Disconnected", twitchIrc: "Disconnected" },
    // Local-clock ms timestamp of the last byte received from the server
    // (snapshot or WS frame). Drives the "last sync" staleness ticker.
    lastRecvMs: 0,
  };

  renderStatus(strip, state.status);
  renderLiveFeed(feedPane, state.feed);
  renderChat(chatPane, state.chat);
  renderScripts(scriptsPane, state.scripts);
  renderSyslog(syslogPane, state.syslog);

  bootstrap(ctx, state, { feedPane, chatPane, scriptsPane, syslogPane, strip, footSync, footer, topbar }, gen)
    .catch(err => {
      console.error("viewer bootstrap failed", err);
      footSync.textContent = "snapshot failed";
    });
}

async function bootstrap(ctx, state, ui, gen) {
  const creds = ctx.getCreds();
  if (!creds?.token) { ctx.onDisconnect(); return; }

  const t0 = performance.now();
  let resp;
  try {
    resp = await fetch(`${apiBase()}/v/api/snapshot`, {
      headers: { "Authorization": `Bearer ${creds.token}` },
    });
  } catch {
    if (gen !== connectionGen) return;
    ui.footSync.textContent = "Hub unreachable";
    return;
  }
  // The await can outlive this dashboard (user hit Disconnect or the view
  // re-rendered mid-flight) — a stale bootstrap must not touch state or,
  // worse, clear a NEWER session's credentials via onDisconnect below.
  if (gen !== connectionGen) return;
  if (resp.status === 401 || resp.status === 403) {
    ctx.onDisconnect();
    return;
  }
  if (!resp.ok) {
    ui.footSync.textContent = `snapshot ${resp.status}`;
    return;
  }
  const snap = await resp.json();
  if (gen !== connectionGen) return;
  const dt = (performance.now() - t0).toFixed(0);
  ui.footSync.textContent = `last sync ${dt}ms`;
  // Seed the staleness clock from the snapshot so the ticker starts sane
  // even if the WS takes a moment to deliver its first frame.
  state.lastRecvMs = Date.now();

  state.feed    = (snap.liveFeed    || []).slice(-MAX_FEED);
  state.chat    = (snap.chat        || []).slice(-MAX_CHAT);
  state.scripts = (snap.scripts     || []);
  state.syslog  = (snap.systemLog   || []).slice(-MAX_LOG);
  state.status  = snap.connectionStatus || state.status;

  renderStatus(ui.strip, state.status);
  renderLiveFeed(ui.feedPane, state.feed);
  renderChat(ui.chatPane, state.chat);
  renderScripts(ui.scriptsPane, state.scripts);
  renderSyslog(ui.syslogPane, state.syslog);

  // Live channel.
  openWs(ctx, state, ui, gen);
}

function openWs(ctx, state, ui, gen, reconnect) {
  // Stale chain — the dashboard re-rendered, disconnected, or auth-
  // terminated since this attempt was scheduled. Never mint a socket for
  // a retired generation.
  if (gen !== connectionGen) return;

  // `reconnect` carries the current backoff delay across attempts.
  // First call from bootstrap leaves it undefined; we seed it here.
  // QC27-06 — `openlessFailures` counts consecutive WS close events that
  // never saw an "open" first, used to detect handshake-401 cascades the
  // browser obfuscates as plain 1006.
  if (!reconnect) reconnect = {
    delayMs: WS_RECONNECT_MIN_MS,
    opened: false,
    openedThisAttempt: false,
    openlessFailures: 0,
  };

  const creds = ctx.getCreds();
  if (!creds?.token) return;

  reconnect.openedThisAttempt = false;

  const wsUrl = `${window.location.protocol === "https:" ? "wss" : "ws"}://${window.location.host}/v/ws`;
  let ws;
  try {
    ws = new WebSocket(wsUrl, [`bearer.${creds.token}`]);
  } catch (err) {
    console.error("ws open failed", err);
    scheduleReconnect(ctx, state, ui, gen, reconnect);
    return;
  }
  activeWs = ws;

  ws.addEventListener("open", () => {
    if (gen !== connectionGen) {
      // Ghost socket from a retired chain — close it and walk away
      // without touching the live dashboard's timers.
      try { ws.close(); } catch { /* best-effort */ }
      return;
    }
    // Healthy connection — reset backoff so the next disconnect retries
    // promptly instead of inheriting a 30s delay from a prior outage.
    reconnect.opened = true;
    reconnect.openedThisAttempt = true;
    reconnect.openlessFailures = 0;
    reconnect.delayMs = WS_RECONNECT_MIN_MS;
    startTimers(ws, state, ui);
  });

  ws.addEventListener("message", evt => {
    // Retired chain — this socket's state and DOM handles belong to a
    // dashboard that no longer exists.
    if (gen !== connectionGen) return;
    // Any inbound frame proves the server is alive — even one we fail to
    // parse below. The staleness ticker reads this local-clock stamp.
    state.lastRecvMs = Date.now();
    let envelope;
    try { envelope = JSON.parse(evt.data); }
    catch { return; }
    let payload;
    try { payload = envelope.payload ? JSON.parse(envelope.payload) : null; }
    catch { payload = null; }
    if (!payload) return;

    switch (envelope.type) {
      case "VIEWER_LIVEFEED":
        if (payload.entry) {
          state.feed.push(payload.entry);
          if (state.feed.length > MAX_FEED) state.feed.shift();
          // P1-28: incremental append — preserve existing row nodes,
          // only insert the new row and trim the head once over cap.
          appendLiveFeed(ui.feedPane, payload.entry, state.feed.length);
        }
        break;
      case "VIEWER_CHAT":
        if (payload.message) {
          state.chat.push(payload.message);
          if (state.chat.length > MAX_CHAT) state.chat.shift();
          appendChat(ui.chatPane, payload.message, state.chat.length);
        }
        break;
      case "VIEWER_SCRIPT_STATUS":
        if (payload.status) {
          // P1-28: scripts list is a sparse table (~tens of rows) and
          // mergeScript can update-in-place OR append, so a full re-paint
          // is still the right call here — it stays under 1ms even at the
          // upper bound. The jank only showed up on the high-cardinality
          // panels (live feed / chat / syslog at >2 msgs/sec, up to 200
          // rows each).
          mergeScript(state.scripts, payload.status);
          renderScripts(ui.scriptsPane, state.scripts);
        }
        break;
      case "VIEWER_SYSTEM_LOG":
        if (payload.entry) {
          state.syslog.push(payload.entry);
          if (state.syslog.length > MAX_LOG) state.syslog.shift();
          appendSyslog(ui.syslogPane, payload.entry, state.syslog.length);
        }
        break;
      case "VIEWER_CONNECTION_STATUS":
        if (payload.status) {
          state.status = payload.status;
          renderStatus(ui.strip, state.status);
        }
        break;
      case "VIEWER_PONG": {
        // payload is our own Date.now() echoed back by the server, so the
        // RTT is measured on a single clock. Guard against a mangled echo:
        // a NaN / negative delta leaves the pill showing its last value.
        const rtt = Date.now() - Number(payload);
        const latEl = ui.topbar && ui.topbar.__phxLatency;
        if (latEl && Number.isFinite(rtt) && rtt >= 0) {
          latEl.textContent = `${rtt}ms`;
        }
        break;
      }
    }
  });

  ws.addEventListener("close", (evt) => {
    // Retired chain — no reconnect, no timer teardown, no DOM writes.
    // A newer generation owns all of those now.
    if (gen !== connectionGen) return;
    // Whatever the close reason, this socket's intervals must die with it
    // — the reconnect path re-creates them on the next successful open.
    stopTimers(ws);
    // QC27-06 — auth-class terminal codes: stop reconnecting, clear the
    // stored token, surface a re-pair message. Without this the loop
    // would hammer a permanently-401 endpoint until the tab closes.
    if (evt && WS_TERMINAL_CLOSE_CODES.has(evt.code)) {
      handleAuthTermination(ctx, ui, `device revoked (ws ${evt.code}) — please re-pair`);
      return;
    }
    // QC27-06 — handshake-401 cascade: browsers map the upgrade-time
    // HTTP 401 to a plain close(1006) with wasClean=false and no
    // preceding "open". A run of these in a row strongly implies the
    // token's no longer valid (server-side revoke, or the device row
    // was rotated). Bail out the same way as an explicit 4401.
    if (!reconnect.openedThisAttempt) {
      reconnect.openlessFailures = (reconnect.openlessFailures || 0) + 1;
      if (reconnect.openlessFailures >= WS_MAX_OPENLESS_RETRIES) {
        handleAuthTermination(
          ctx, ui,
          `connection rejected ${reconnect.openlessFailures}× — token may be revoked, please re-pair`);
        return;
      }
    }
    scheduleReconnect(ctx, state, ui, gen, reconnect);
  });
  ws.addEventListener("error", () => { /* close handler retries */ });
}

function scheduleReconnect(ctx, state, ui, gen, reconnect) {
  // Retired chain — don't repaint the footer or queue another attempt.
  // openWs re-checks the generation when the timeout fires, so a bump
  // that lands during the backoff window also kills the chain.
  if (gen !== connectionGen) return;
  const delay = Math.round(reconnect.delayMs);
  ui.footSync.textContent = `ws closed — reconnecting in ${(delay / 1000).toFixed(1)}s`;
  setTimeout(() => openWs(ctx, state, ui, gen, reconnect), delay);
  // Grow the next delay; cap at the ceiling. Only growth-on-fail —
  // a successful "open" resets the value in the open-listener above.
  reconnect.delayMs = Math.min(reconnect.delayMs * WS_RECONNECT_FACTOR, WS_RECONNECT_MAX_MS);
}

// QC27-06 — terminal-auth handler: surface the cause in the status strip,
// then drop the stored credentials and bounce back to the pair screen.
// ctx.onDisconnect() clears localStorage and re-renders renderPair, so the
// next paint is the PIN form with a recoverable next step for the user.
function handleAuthTermination(ctx, ui, message) {
  console.warn("viewer: terminating reconnect —", message);
  // Retire the whole connection chain: no handler, timer, or queued
  // reconnect from this socket may run again. The socket is closed too in
  // case the server left it half-open (terminal codes usually arrive on
  // an already-closed socket, so this is belt-and-braces).
  connectionGen++;
  stopTimers();
  try { activeWs?.close(); } catch { /* already closed */ }
  activeWs = null;
  try { ui.footSync.textContent = message; } catch {}
  try { ctx.onDisconnect(); } catch (err) { console.error(err); }
}

function mergeScript(list, incoming) {
  const i = list.findIndex(s => s.path === incoming.path || s.name === incoming.name);
  if (i >= 0) list[i] = incoming;
  else        list.push(incoming);
}

// ── status strip ─────────────────────────────────────────────────────
function renderStatus(host, status) {
  host.innerHTML = "";
  const dots = [
    { state: stateClass(status.streamerBot), label: "Streamer.bot", sub: "ws://127.0.0.1:8080" },
    { state: stateClass(status.hudOverlay),  label: "HUD Overlay",  sub: "port 18080" },
    { state: stateClass(status.ipcBus),      label: "IPC Bus",      sub: "port 18081" },
    { state: stateClass(status.twitchIrc),   label: "Twitch IRC",   sub: "tmi.twitch.tv" },
  ];
  for (const d of dots) {
    const node = el("div", `status-dot ${d.state}`);
    node.appendChild(el("span", "swatch"));
    const lines = el("div", "lines");
    lines.appendChild(el("span", "label", d.label));
    lines.appendChild(el("span", "sub",   d.sub));
    node.appendChild(lines);
    host.appendChild(node);
  }
  const right = el("span", "right");
  right.textContent = "viewer.live · synced";
  host.appendChild(right);
}
function stateClass(s) {
  switch ((s || "").toLowerCase()) {
    case "connected": return "ok";
    case "degraded": case "connecting": return "warn";
    case "errored": return "err";
    default: return "idle";
  }
}

// ── live feed ────────────────────────────────────────────────────────
// P1-28: full render only on bootstrap / re-paint. Live WS deltas go
// through appendLiveFeed below so we don't rebuild 200 rows per message.
function renderLiveFeed(host, feed) {
  host.innerHTML = "";
  const header = panelHeader("01", "Live Feed", `${feed.length} events · live`);
  host.appendChild(header);
  const body = el("div", "panel-body");
  const empty = el("div", "panel-empty", "No events yet.");
  if (feed.length === 0) body.appendChild(empty);
  for (const e of feed) body.appendChild(buildLiveFeedRow(e));
  host.appendChild(body);
  // Cache the live-update handles on the host so appendLiveFeed can find
  // them without re-querying the DOM (and so we know whether the panel
  // has been fully rendered yet).
  host.__phxBody   = body;
  host.__phxHeader = header;
  host.__phxEmpty  = empty;
  scheduleScroll(body);
}
function buildLiveFeedRow(e) {
  const row = el("div", "livefeed-row");
  row.appendChild(el("span", "time", formatClock(e.timestamp)));
  row.appendChild(el("span", `kind ${kindClass(e.kind)}`, (e.kind || "").toUpperCase()));
  row.appendChild(el("span", "who", e.who || ""));
  row.appendChild(el("span", "detail", e.detail || ""));
  return row;
}
function appendLiveFeedRow(body, e) { body.appendChild(buildLiveFeedRow(e)); }
function appendLiveFeed(host, entry, total) {
  const body = host.__phxBody;
  if (!body) { renderLiveFeed(host, [entry]); return; }
  // First row drops the "No events yet." placeholder.
  if (host.__phxEmpty && host.__phxEmpty.parentNode === body) {
    body.removeChild(host.__phxEmpty);
  }
  appendLiveFeedRow(body, entry);
  trimRows(body, MAX_FEED);
  updateHeaderRight(host.__phxHeader, `${total} events · live`);
  scheduleScroll(body);
}
function kindClass(k) { return (k || "").toLowerCase(); }

// ── chat ─────────────────────────────────────────────────────────────
function renderChat(host, chat) {
  host.innerHTML = "";
  const header = panelHeader("02", "Chat", `${chat.length} / 2000 · live`);
  host.appendChild(header);
  const body = el("div", "panel-body");
  const empty = el("div", "panel-empty", "Chat is quiet.");
  if (chat.length === 0) body.appendChild(empty);
  for (const m of chat) body.appendChild(buildChatRow(m));
  host.appendChild(body);

  const sendrow = el("div", "chat-sendrow");
  const lock = el("div", "lock");
  lock.setAttribute("aria-label", "sending disabled");
  // P1-22 textContent discipline: rebuild the lock indicator via DOM
  // primitives rather than innerHTML so it doesn't widen the XSS surface
  // if a future caller substitutes the literal.
  const diamond = document.createElement("span");
  diamond.textContent = "◆";
  lock.appendChild(diamond);
  const lockMsg = document.createElement("span");
  lockMsg.textContent = "Send-as-bot disabled · viewer is read-only";
  lock.appendChild(lockMsg);
  sendrow.appendChild(lock);
  const btn = document.createElement("button");
  btn.disabled = true;
  btn.textContent = "Send";
  sendrow.appendChild(btn);
  host.appendChild(sendrow);

  host.__phxBody   = body;
  host.__phxHeader = header;
  host.__phxEmpty  = empty;
  scheduleScroll(body);
}
function buildChatRow(m) {
  const row = el("div", "chat-row");
  const role = (m.role || "Viewer").toLowerCase();
  const badge = badgeFor(role);
  if (badge.label) {
    row.appendChild(el("span", `badge ${role}`, badge.label));
  } else {
    const empty = el("span", "badge empty");
    empty.textContent = " ";
    row.appendChild(empty);
  }
  row.appendChild(el("span", "who",  m.username || ""));
  row.appendChild(el("span", "body", m.body || ""));
  return row;
}
function appendChat(host, msg, total) {
  const body = host.__phxBody;
  if (!body) { renderChat(host, [msg]); return; }
  if (host.__phxEmpty && host.__phxEmpty.parentNode === body) {
    body.removeChild(host.__phxEmpty);
  }
  body.appendChild(buildChatRow(msg));
  trimRows(body, MAX_CHAT);
  updateHeaderRight(host.__phxHeader, `${total} / 2000 · live`);
  scheduleScroll(body);
}
function badgeFor(role) {
  switch (role) {
    case "broadcaster": return { label: "B" };
    case "mod":         return { label: "M" };
    case "vip":         return { label: "V" };
    case "sub":         return { label: "S" };
    case "bot":         return { label: "B" };
    default:            return { label: "" };
  }
}

// ── scripts ──────────────────────────────────────────────────────────
function renderScripts(host, scripts) {
  host.innerHTML = "";
  const errored = scripts.filter(s => (s.state || "").toLowerCase() === "errored").length;
  host.appendChild(panelHeader("03", "Scripts", `${scripts.length} loaded · ${errored} errored`));
  const headerRow = el("div", "scripts-headerrow");
  for (const h of ["", "Script", "Status", "CPU", "RAM", "Runs"]) headerRow.appendChild(el("span", "", h));
  host.appendChild(headerRow);

  const body = el("div", "panel-body");
  if (scripts.length === 0) body.appendChild(el("div", "panel-empty", "No PhoenixScript files loaded."));
  for (const s of scripts) {
    const row = el("div", "scripts-row" + (s.enabled ? "" : " disabled"));
    row.appendChild(el("span", "toggle"));
    row.appendChild(el("span", "name", s.name || ""));
    const state = el("span", `state ${(s.state || "idle").toLowerCase()}`);
    state.appendChild(el("span", "swatch"));
    state.appendChild(el("span", "", (s.state || "idle").toLowerCase()));
    row.appendChild(state);
    row.appendChild(el("span", "num", `${(s.lastCpuMs || 0).toFixed(0)}ms`));
    row.appendChild(el("span", "num", formatRam(s.lastRamBytes || 0)));
    row.appendChild(el("span", "num", String(s.runCount ?? 0)));
    body.appendChild(row);
  }
  host.appendChild(body);
}
function formatRam(bytes) {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)}K`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}M`;
}

// ── syslog ───────────────────────────────────────────────────────────
function renderSyslog(host, log) {
  host.innerHTML = "";
  const header = panelHeader("04", "System Log", `${log.length} entries`);
  host.appendChild(header);
  const body = el("div", "panel-body");
  const empty = el("div", "panel-empty", "No system log entries.");
  if (log.length === 0) body.appendChild(empty);
  for (const e of log) body.appendChild(buildSyslogRow(e));
  host.appendChild(body);
  host.__phxBody   = body;
  host.__phxHeader = header;
  host.__phxEmpty  = empty;
  scheduleScroll(body);
}
function buildSyslogRow(e) {
  const row = el("div", "syslog-row");
  row.appendChild(el("span", "time", formatClock(e.timestamp)));
  row.appendChild(el("span", `level ${(e.level || "info").toLowerCase()}`, (e.level || "info")));
  row.appendChild(el("span", "source", e.source || ""));
  row.appendChild(el("span", "msg",    e.message || ""));
  return row;
}
function appendSyslog(host, entry, total) {
  const body = host.__phxBody;
  if (!body) { renderSyslog(host, [entry]); return; }
  if (host.__phxEmpty && host.__phxEmpty.parentNode === body) {
    body.removeChild(host.__phxEmpty);
  }
  body.appendChild(buildSyslogRow(entry));
  trimRows(body, MAX_LOG);
  updateHeaderRight(host.__phxHeader, `${total} entries`);
  scheduleScroll(body);
}

// ── shared ───────────────────────────────────────────────────────────
function panelHeader(eyebrow, title, right) {
  const h = el("div", "panel-header");
  h.appendChild(el("span", "eyebrow", eyebrow));
  h.appendChild(el("span", "title",   title));
  const r = el("span", "right", right || "");
  h.appendChild(r);
  // Stash the right-side caption so updateHeaderRight can tweak it
  // without re-walking the header's children.
  h.__phxRight = r;
  return h;
}
// P1-28: cheap mutation of the panel-header counter — no re-render of
// the row list. Tolerates a missing header (defensive guard for the
// append-without-render path).
function updateHeaderRight(header, text) {
  if (!header || !header.__phxRight) return;
  header.__phxRight.textContent = text;
}
// P1-28: trim head when the body has more than `cap` rendered rows. The
// "panel-empty" placeholder is never present at this point (callers
// remove it before append), so first-child is always a real row.
function trimRows(body, cap) {
  while (body.childElementCount > cap) {
    body.removeChild(body.firstElementChild);
  }
}
// P1-28: batch scroll-to-bottom with the next paint so a flurry of
// messages doesn't trigger N separate layout passes. WebKit + Blink
// both coalesce the scrollTop writes into one when they land in the
// same rAF tick.
function scheduleScroll(body) {
  if (!body || body.__phxScrollPending) return;
  body.__phxScrollPending = true;
  requestAnimationFrame(() => {
    body.__phxScrollPending = false;
    body.scrollTop = body.scrollHeight;
  });
}
function formatClock(ts) {
  if (!ts) return "";
  try {
    const d = new Date(ts);
    if (Number.isNaN(d.getTime())) return "";
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
  } catch { return ""; }
}
