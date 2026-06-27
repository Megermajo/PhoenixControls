// Phoenix Controls — Viewer SPA entry.
// Resolves the channel from the URL path (/v/{channel}), looks for a
// stored token in localStorage, and routes to either the pair flow or
// the connected dashboard. No build step — hand-written ES modules.

import { renderPair }      from "./pair.js";
import { renderConnected } from "./connected.js";

const PATH_PREFIX = "/v/";

function parseChannelFromPath() {
  const path = window.location.pathname;
  if (!path.startsWith(PATH_PREFIX)) return "channel";
  const tail = path.slice(PATH_PREFIX.length);
  const slash = tail.indexOf("/");
  return slash < 0 ? tail : tail.slice(0, slash);
}

function tokenStorageKey(channel) {
  return `phx-viewer:${channel}:credentials`;
}

export function loadCreds(channel) {
  try {
    const raw = localStorage.getItem(tokenStorageKey(channel));
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}

export function saveCreds(channel, creds) {
  // SECURITY: token is in localStorage; XSS surface is mitigated by
  // textContent discipline — see audit P1-22 for the migration plan
  // (httpOnly cookie + same-site server-issued session). Until that
  // lands, every render path in this bundle MUST use textContent /
  // DOM-API construction rather than innerHTML on user-derived strings.
  try { localStorage.setItem(tokenStorageKey(channel), JSON.stringify(creds)); }
  catch { /* private mode / quota */ }
}

export function clearCreds(channel) {
  try { localStorage.removeItem(tokenStorageKey(channel)); } catch {}
}

export function apiBase() {
  // Same-origin: the viewer bundle is served by ViewerServer itself,
  // so an empty base resolves to whichever scheme/host/port loaded the
  // SPA (LAN or loopback alike).
  return "";
}

async function bootstrap() {
  const channel = parseChannelFromPath();
  document.title = `Phoenix Controls — ${channel}`;
  const root = document.getElementById("app");
  const ctx = {
    channel,
    onPaired: (creds) => {
      saveCreds(channel, creds);
      renderConnected(root, ctx);
    },
    onDisconnect: () => {
      clearCreds(channel);
      renderPair(root, ctx);
    },
    getCreds: () => loadCreds(channel),
  };

  const existing = loadCreds(channel);
  if (existing && existing.token) {
    renderConnected(root, ctx);
  } else {
    renderPair(root, ctx);
  }
}

bootstrap();
