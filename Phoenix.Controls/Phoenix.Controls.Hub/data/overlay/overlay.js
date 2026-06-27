/**
 * overlay.js — Legacy shim, intentionally inert.
 *
 * The active overlay runtime is compositor.js, which index.html loads
 * directly via <script src="/compositor.js"></script>. This file exists
 * only so that any external bookmark / OBS browser source still pointing
 * at overlay.js gets a 200 OK instead of a 404; the shim does nothing.
 *
 * Pre-T14b this fallback re-added compositor.js when window.SovereignCompositor
 * was undefined, but compositor.js never published that global so the
 * conditional never fired — it was effectively dead. Removed during the
 * Sovereign → Phoenix Controls rebrand sweep ([MINE-06]).
 */
(function () { /* no-op */ })();
