// PIN-pairing UX. Renders the centered card from remote-viewer.jsx and
// drives the /v/api/pair/{begin,complete} round-trip when the user
// finishes typing the 6-digit code. Optimistic — only success flips the
// SPA to the connected state.

import { apiBase } from "./app.js";

const PIN_LEN = 6;

export function renderPair(root, ctx) {
  root.innerHTML = "";

  const topbar = topbarMarkup(ctx.channel, null);
  root.appendChild(topbar);

  const stage = el("div", "pair-stage");
  stage.appendChild(el("div", "watermark"));
  stage.appendChild(el("div", "rays"));

  const card = el("div", "pair-card");
  card.appendChild(el("div", "mark"));
  card.appendChild(el("div", "eyebrow", "Pair this device"));
  const h1 = el("h1");
  h1.textContent = "Enter your 6-digit pairing PIN";
  card.appendChild(h1);
  const lead = el("p", "lead");
  lead.appendChild(document.createTextNode("Device pairing is not yet available in this version of Phoenix Controls."));
  lead.appendChild(document.createElement("br"));
  lead.appendChild(document.createTextNode("A future update will let Hub issue the pairing PIN for this screen."));
  card.appendChild(lead);

  const pinRow = el("div", "pin-row");
  const cells = [];
  for (let i = 0; i < PIN_LEN; i++) {
    const c = el("div", "pin-cell");
    pinRow.appendChild(c);
    cells.push(c);
  }
  card.appendChild(pinRow);

  // Hidden input is the authoritative editor; cells reflect its state.
  // Single input means iOS / desktop / mobile all just work.
  const input = document.createElement("input");
  input.type = "tel";
  input.inputMode = "numeric";
  input.autocomplete = "one-time-code";
  input.maxLength = PIN_LEN;
  input.className = "pin-input";
  card.appendChild(input);

  const meta = el("div", "pair-meta");
  const metaLabel = document.createElement("span");
  metaLabel.textContent = "Connecting via";
  meta.appendChild(metaLabel);
  const metaHost = document.createElement("strong");
  metaHost.textContent = `LAN · ${window.location.host}`;
  meta.appendChild(metaHost);
  card.appendChild(meta);

  const foot = el("div", "pair-foot");
  foot.appendChild(document.createTextNode("Codes expire 60 seconds after they are issued."));
  card.appendChild(foot);

  stage.appendChild(card);
  root.appendChild(stage);

  pinRow.addEventListener("click", () => input.focus());
  // Auto-focus on render so a viewer can just start typing.
  setTimeout(() => input.focus(), 0);

  function paint() {
    const v = input.value.replace(/\D/g, "").slice(0, PIN_LEN);
    input.value = v;
    cells.forEach((cell, i) => {
      cell.textContent = v[i] || "";
      cell.classList.toggle("filled", !!v[i]);
      cell.classList.toggle("cursor", i === v.length && document.activeElement === input);
    });
  }

  let submitting = false;
  input.addEventListener("input", async () => {
    paint();
    if (input.value.length === PIN_LEN && !submitting) {
      submitting = true;
      foot.textContent = "Verifying…";
      try {
        const creds = await pairFlow(ctx.channel, input.value, deviceLabel());
        if (creds) {
          ctx.onPaired(creds);
          return;
        }
        showError(foot, "Invalid or expired code. Re-enter the PIN shown in Hub.");
        input.value = "";
        paint();
      } catch (err) {
        showError(foot, "Network error reaching Hub. Confirm the channel URL and try again.");
      } finally {
        submitting = false;
        input.focus();
      }
    }
  });
  input.addEventListener("focus", paint);
  input.addEventListener("blur",  paint);
  paint();
}

async function pairFlow(channel, code, deviceLabel) {
  const begin = await fetch(`${apiBase()}/v/api/pair/begin`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ deviceLabel }),
  });
  if (!begin.ok) return null;
  const beginJson = await begin.json();
  const pairingId = beginJson.pairingId;
  if (!pairingId) return null;

  const complete = await fetch(`${apiBase()}/v/api/pair/complete`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      pairingId,
      code,
      devicePubId: devicePubId(),
    }),
  });
  if (!complete.ok) return null;
  const data = await complete.json();
  if (!data.token || !data.deviceId) return null;
  return {
    token: data.token,
    deviceId: data.deviceId,
    label: deviceLabel,
    pairedAt: Date.now(),
  };
}

function deviceLabel() {
  // Best-effort label so the streamer can recognize the device in Hub
  // UI. Not a security boundary — server uses the bearer token.
  const ua = navigator.userAgent || "device";
  if (/iPad/.test(ua))   return "iPad";
  if (/iPhone/.test(ua)) return "iPhone";
  if (/Android/.test(ua))return "Android";
  if (/Windows/.test(ua))return "Windows browser";
  if (/Macintosh/.test(ua)) return "Mac browser";
  return "Browser device";
}

function devicePubId() {
  // Stable per-browser ID stored in localStorage. Required by the
  // pair/complete contract. Re-paired devices keep the same id so
  // Hub can recognize a re-pair as "same device".
  const KEY = "phx-viewer:devicePubId";
  let id = null;
  try { id = localStorage.getItem(KEY); } catch { /* private mode */ }
  if (!id) {
    const buf = crypto.getRandomValues(new Uint8Array(8));
    id = Array.from(buf, b => b.toString(16).padStart(2, "0")).join("");
    // P1-22 safeguard: Safari private mode + Firefox strict-tracking can
    // throw SecurityError / QuotaExceededError here. Swallow + warn so
    // the pair flow still completes with an ephemeral id rather than
    // hanging the user on a silent exception.
    try { localStorage.setItem(KEY, id); }
    catch (e) { console.warn("devicePubId: localStorage unavailable, using ephemeral id", e); }
  }
  return id;
}

function showError(footEl, msg) {
  // Build via DOM API rather than innerHTML — even though msg is
  // currently a hard-coded literal, this removes the regression vector
  // if a future caller passes server-derived text.
  footEl.textContent = "";
  const span = document.createElement("span");
  span.className = "err";
  span.textContent = String(msg);
  footEl.appendChild(span);
}

// Re-export the topbar helper so renderConnected can reuse it.
export function topbarMarkup(channel, latencyMs) {
  const top = el("div", "viewer-topbar");
  top.appendChild(el("div", "viewer-mark"));
  const brand = el("span", "viewer-brand");
  brand.appendChild(document.createTextNode("PHOENIX "));
  const muted = el("span", "muted");
  muted.textContent = "CONTROLS";
  brand.appendChild(muted);
  top.appendChild(brand);
  const dot = document.createTextNode(" · ");
  top.appendChild(dot);
  const ch = el("span", "viewer-channel");
  ch.textContent = channel;
  top.appendChild(ch);
  top.appendChild(el("div", "viewer-spacer"));

  const ro = el("div", "viewer-pill readonly");
  ro.appendChild(el("span", "dot"));
  ro.appendChild(document.createTextNode("Read-only"));
  top.appendChild(ro);

  const mp = el("div", "viewer-pill connection");
  const host = window.location.host || "loopback";
  mp.appendChild(el("span", "dot"));
  mp.appendChild(document.createTextNode(`LAN · ${host}`));
  top.appendChild(mp);

  const lat = el("span", "viewer-latency");
  lat.appendChild(document.createTextNode("↔ "));
  const latStrong = document.createElement("strong");
  latStrong.textContent = latencyMs == null ? "—" : `${latencyMs}ms`;
  lat.appendChild(latStrong);
  top.appendChild(lat);
  // Cache the latency <strong> on the root (same __phx handle pattern as
  // the panel headers) so connected.js can patch it in place on each
  // VIEWER_PONG. The pair screen never touches it and keeps the null "—".
  top.__phxLatency = latStrong;
  return top;
}

export function el(tag, cls, text) {
  const node = document.createElement(tag);
  if (cls)  node.className = cls;
  if (text) node.textContent = text;
  return node;
}
