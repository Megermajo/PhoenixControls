using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Collapses the per-platform stream-lifecycle events into the single
    // live/offline EDGES the Hub's one-flag consumers actually mean.
    //
    // The naive one-boolean mapping (any GoingLive => live, any SessionEnd =>
    // offline) breaks multi-platform restreaming: ending the YouTube broadcast
    // while Twitch is still live would flip every live gate offline (subathon
    // frozen, Scheduling stops posting, {uptime} blank), and the NEXT YouTube
    // go-live would look like a fresh offline->live transition — wiping the
    // User-Management welcomed-set mid-stream. This tracker keeps the set of
    // currently-live platforms and reports an edge only when the set becomes
    // non-empty (+1) or empty (-1); everything in between is 0 = no change.
    // Bonus: a platform re-emitting GoingLive for an already-live stream is a
    // no-edge, so the welcomed-set reset can no longer double-fire either.
    //
    // ── Startup reconciliation ──────────────────────────────────────────────
    // The live set is per-process state, so a Hub restart starts it EMPTY — and
    // the platforms only push go-live at the real transition, so nothing PUSHED
    // ever refills it. After a mid-stream restart every later session end
    // therefore looked like an end for a platform that was never live (edge 0):
    // the restream leg ending was silently right, but the LAST leg ending was
    // silently wrong, so the stream's genuine offline edge was never reported
    // at all and the live gates stayed armed until the process died.
    //
    // Streamer.bot still exposes no NATIVE "is my channel live" request. The one
    // live-state surface in the suite is the action pack's custom-C# "Phoenix:
    // Get Stream Status" (Twitch Helix /streams through Streamer.bot's own
    // credentials), which ScriptManager.ReconcileStreamStateAsync polls and feeds
    // back in here as a Twitch GoingLive Note. That path answers for TWITCH only,
    // and only when the pack is imported, SB is connected and the action reports
    // phx_stream_known == "1" — so it can supplement this tracker but never be
    // its restore mechanism. The set is therefore still reconciled from a tiny
    // snapshot this tracker writes itself: the live set plus a UTC stamp,
    // rewritten on every set change AND heartbeated once a minute while live.
    // The stamp therefore measures HUB DOWNTIME, not session length — a
    // multi-day subathon reconciles just as well as a 30-second restart, while
    // an unclean kill hours ago does not. The snapshot is trusted only inside
    // <see cref="TrustWindow"/>; anything older (or a missing / truncated /
    // clock-skewed one) is UNKNOWN, and unknown starts EMPTY.
    //
    // Restored entries are PROVISIONAL (tracked in _restoredOnly) and can never
    // swallow a go-live edge. The snapshot proves the HUB was live when it last
    // wrote, not that the STREAM still is: a session end that arrived while the
    // Hub was down is gone forever. Counting a restored platform as observed-live
    // would therefore make a stale "live" permanent — the next GENUINE go-live
    // would report 0 and every single-flag consumer (welcomed-set reset, Timer,
    // Scheduling, Loyalty) would sit offline for that whole stream. Losing a real
    // edge is strictly worse than re-running a per-stream reset, so a GoingLive
    // always promotes its platform out of _restoredOnly and the +1 is measured
    // over the OBSERVED set alone. Reconciliation only ever fixes the END
    // arithmetic — which is where the restream damage actually was.
    //
    // Restoring the set does not by itself re-arm the consumers' own flags — this
    // tracker only reports edges. Those gates (MarkBroadcasterLive / Timer /
    // Scheduling / Ranks / Loyalty / User-Management SetStreamLive) are pushed
    // from ScriptManager's dispatch, and what closes the restart gap for them is
    // ScriptManager.ReconcileStreamStateAsync: on Streamer.bot connect and every
    // 60s after, it asks "Phoenix: Get Stream Status" and, on an authoritative
    // live answer, latches MarkBroadcasterLive to the REAL started_at and arms
    // exactly three consumers — Timer, Scheduling, Ranks. (The mirror-image
    // authoritative-offline answer pushes those same three offline, and only
    // when this tracker agrees nothing is live.)
    //
    // ★ User-Management and Loyalty are left out of that fan-out ON PURPOSE —
    // this is not an oversight to "fix". Their false→true edge runs a DESTRUCTIVE
    // per-stream reset:
    //   • UserManagementService.SetStreamLive(true) clears the in-memory
    //     welcomed-set AND fires DB.ClearUserMgmtSeenAsync() — i.e. re-greets the
    //     whole of chat mid-stream.
    //   • LoyaltyService.SetStreamLive(true) clears the per-stream follow dedupe
    //     and the per-stream reward-quantity counters — reopening a follow payout
    //     and limited-quantity rewards that were already claimed this stream.
    // A reconcile cannot tell "you were live the whole time" apart from a genuine
    // go-live, so those two keep waiting for a real platform edge.
    //
    // Residual, and bounded: a platform whose end was missed while the Hub was
    // down lingers in the set as a phantom, so an unrelated platform's last-down
    // can read as "others still live". It drains at that platform's own next
    // session end, or at the first launch more than TrustWindow after the last
    // write — and it can no longer cost a go-live edge.
    internal sealed class LivePlatformEdgeTracker : IDisposable
    {
        /// <summary>
        /// How stale the snapshot may be and still be believed. Sized as a HUB
        /// DOWNTIME budget (restart, or an auto-update relaunch) rather than a
        /// stream-length one — the heartbeat keeps the stamp fresh for as long
        /// as the Hub is up, so a longer window would only ever add credit to a
        /// Hub that was DOWN, which is precisely the case we can't vouch for.
        /// </summary>
        private static readonly TimeSpan TrustWindow = TimeSpan.FromMinutes(10);

        /// <summary>Re-stamp cadence while any platform is live. Well inside
        /// <see cref="TrustWindow"/> so a crash loses at most one interval.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

        private readonly object _gate = new();
        private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

        // The subset of _live that was RESTORED from the snapshot rather than seen
        // going live in this process. Invariant: _restoredOnly ⊆ _live — every
        // mutation below maintains it, which is what lets the observed count be
        // read as the plain (_live.Count - _restoredOnly.Count).
        private readonly HashSet<string> _restoredOnly = new(StringComparer.OrdinalIgnoreCase);

        // Serializes the one-shot reconciliation so a second caller WAITS for it
        // rather than racing past on an empty set. Lock order is always
        // _reconcileGate → _gate; nothing takes _reconcileGate while holding _gate.
        private readonly object _reconcileGate = new();
        private volatile bool _reconciled;

        // "" disables persistence entirely (AppData unavailable — sandboxed
        // environments / CI). Everything else still works, just without the
        // cross-restart memory.
        private readonly string _snapshotPath;
        private System.Threading.Timer? _heartbeat;
        private int _persistFailWarned;
        private bool _disposed;

        /// <summary>Production ctor — snapshots into %AppData%/PhoenixControls/Hub/.</summary>
        public LivePlatformEdgeTracker() : this(DefaultSnapshotPath()) { }

        /// <summary>
        /// Test/host ctor. Pass "" for a purely in-memory tracker (no snapshot
        /// read, no writes, no heartbeat) — unit tests must not touch the real
        /// user state.
        /// </summary>
        internal LivePlatformEdgeTracker(string? snapshotPath)
            => _snapshotPath = snapshotPath ?? "";

        /// <summary>
        /// Notes one lifecycle event and returns the edge it produced:
        /// +1 = offline→live (first OBSERVED platform up — a platform merely
        /// restored from the previous session's snapshot does not count as
        /// observed, see the type header), -1 = live→offline (last platform
        /// down), 0 = no state change (already observed live / other platforms
        /// still live / not a lifecycle event / unknown platform).
        /// </summary>
        public int Note(StreamLifecycle.Kind kind, string? platform)
        {
            if (kind == StreamLifecycle.Kind.None || string.IsNullOrEmpty(platform)) return 0;
            // Before _gate: reconciliation reads/writes the snapshot and logs, and
            // the edge this call is about to report is only meaningful once the
            // startup state is established.
            EnsureReconciled();
            lock (_gate)
            {
                bool changed;
                int edge;
                if (kind == StreamLifecycle.Kind.GoingLive)
                {
                    // Measured over the OBSERVED platforms only (_live minus the
                    // provisional restored ones) and BEFORE the mutation below: a
                    // restored entry must never turn the genuine offline→live edge
                    // into a 0, because a session end missed while the Hub was down
                    // would otherwise strand every live gate for the whole stream.
                    bool wasEmpty = _live.Count == _restoredOnly.Count;
                    // Exactly one of these can be true, and either one means the
                    // platform is only NOW observed live: promoted out of the
                    // restored set, or added cold. Both false = already observed,
                    // i.e. the benign re-emit that must stay a no-edge.
                    bool promoted = _restoredOnly.Remove(platform);
                    changed = _live.Add(platform);
                    edge = (changed || promoted) && wasEmpty ? +1 : 0;
                }
                else
                {
                    // SessionEnd — an end for a platform never seen live is ignored
                    // (e.g. Hub started mid-stream and only caught the tail). Drops
                    // the restored marker in lockstep so _restoredOnly ⊆ _live holds.
                    _restoredOnly.Remove(platform);
                    changed = _live.Remove(platform);
                    edge = changed && _live.Count == 0 ? -1 : 0;
                }
                // Persist on every SET change, not just on edges: {twitch, youtube}
                // and {twitch} restore differently, and only the full set can tell
                // the next process which leg ending is the real offline edge. A bare
                // promotion out of _restoredOnly leaves _live untouched and needs no
                // write — observed-ness is per-process by design, the next start
                // reads every restored platform back as provisional again.
                if (changed)
                {
                    PersistLocked();
                    SyncHeartbeatLocked();
                }
                return edge;
            }
        }

        /// <summary>
        /// True while any platform is live (informational), restored platforms
        /// included — this is the SET question the session-end arithmetic asks,
        /// not the observed-only one the go-live edge asks.
        /// </summary>
        public bool AnyLive
        {
            get
            {
                EnsureReconciled();
                lock (_gate) return _live.Count > 0;
            }
        }

        /// <summary>Stops the heartbeat. The production instance lives as long as
        /// the Hub process; tests dispose so no timer outlives the assertion.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _heartbeat?.Dispose();
                _heartbeat = null;
            }
        }

        // ── Reconciliation ──────────────────────────────────────────────────

        private void EnsureReconciled()
        {
            if (_reconciled) return;
            string? note;
            lock (_reconcileGate)
            {
                if (_reconciled) return;
                try
                {
                    note = ReconcileFromSnapshot();
                }
                catch (Exception ex)
                {
                    // A truncated/foreign snapshot is UNKNOWN state, not a fault to
                    // propagate into event dispatch — start empty and say why once.
                    GlobalLogger.Error("LivePlatformEdgeTracker",
                        $"live-state snapshot could not be read from '{_snapshotPath}' — starting from offline", ex);
                    note = null;
                }
                finally
                {
                    // One-shot either way: a snapshot that can't be read now won't
                    // read better on the next event, and retrying per event would
                    // put file I/O on the dispatch path.
                    _reconciled = true;
                }
            }
            if (note != null)
                GlobalLogger.Log(note, "LivePlatformEdgeTracker", LogLevel.Communication);
        }

        /// <summary>
        /// Seeds the live set from the previous session's snapshot. Returns the
        /// one-line note to log (outside every lock), or null when there was
        /// nothing trustworthy to restore.
        /// </summary>
        private string? ReconcileFromSnapshot()
        {
            if (_snapshotPath.Length == 0 || !File.Exists(_snapshotPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(_snapshotPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("stampUtc", out var stampEl)
                || stampEl.ValueKind != JsonValueKind.String
                || !stampEl.TryGetDateTimeOffset(out var stamp))
                return null;

            TimeSpan age = DateTimeOffset.UtcNow - stamp;
            // A negative age means the wall clock moved backwards (DST / an NTP
            // correction) — as unusable as an old stamp. Both are UNKNOWN, and
            // unknown starts offline.
            if (age < TimeSpan.Zero || age > TrustWindow)
            {
                GlobalLogger.Log(
                    $"Live-state snapshot ignored ({FormatAge(age)} old, trusted up to {FormatAge(TrustWindow)}) — starting from offline.",
                    "LivePlatformEdgeTracker", LogLevel.Debug);
                return null;
            }

            var restored = new List<string>();
            if (root.TryGetProperty("platforms", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    string? p = el.GetString();
                    if (!string.IsNullOrWhiteSpace(p)) restored.Add(p.Trim());
                }
            }
            // Empty = the previous session ended cleanly offline. Nothing to
            // restore, and no note worth a log line.
            if (restored.Count == 0) return null;

            lock (_gate)
            {
                foreach (var p in restored)
                {
                    _live.Add(p);
                    // Provisional until a real go-live promotes it — the restored set
                    // fixes end arithmetic only, never the go-live edge.
                    _restoredOnly.Add(p);
                }
                // Re-stamp NOW so a second restart inside the window reconciles too
                // (freshness is the whole basis for trusting the file), and start
                // the heartbeat that keeps it fresh for the rest of the session.
                PersistLocked();
                SyncHeartbeatLocked();
            }

            return $"Live-state reconciled after restart: still live on {string.Join(", ", restored)} " +
                   $"(snapshot {FormatAge(age)} old). The stream's real session end now reports offline " +
                   "once instead of being ignored; a go-live still counts as a fresh start, since a session " +
                   "end missed while the Hub was down cannot be told apart from a stream that never stopped.";
        }

        // ── Snapshot persistence ────────────────────────────────────────────

        private void PersistLocked()
        {
            if (_snapshotPath.Length == 0) return;
            var platforms = new string[_live.Count];
            _live.CopyTo(platforms);
            try
            {
                string json = JsonSerializer.Serialize(new { platforms, stampUtc = DateTimeOffset.UtcNow });
                // Temp-then-Move so the next Hub start never parses a half-written
                // file — same atomic-swap shape as ScriptRegistry.PersistStatesNow.
                string tmp = _snapshotPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _snapshotPath, overwrite: true);
            }
            catch (Exception ex)
            {
                // One-time: the heartbeat retries every minute, so a locked or full
                // disk would otherwise turn a single failure into a log flood.
                if (Interlocked.Exchange(ref _persistFailWarned, 1) == 0)
                    GlobalLogger.Error("LivePlatformEdgeTracker",
                        $"live-state snapshot write failed for '{_snapshotPath}' — a mid-stream Hub restart will start from offline", ex);
            }
        }

        private void SyncHeartbeatLocked()
        {
            bool live = _live.Count > 0;
            if (live && _heartbeat is null && _snapshotPath.Length > 0 && !_disposed)
                _heartbeat = new System.Threading.Timer(
                    _ => Heartbeat(), null, HeartbeatInterval, HeartbeatInterval);
            else if (!live && _heartbeat is not null)
            {
                // Offline: the snapshot already records the empty set, so there is
                // nothing left to keep fresh.
                _heartbeat.Dispose();
                _heartbeat = null;
            }
        }

        private void Heartbeat()
        {
            lock (_gate)
            {
                if (_disposed || _live.Count == 0) return;
                PersistLocked();
            }
        }

        // ── Paths / formatting ──────────────────────────────────────────────

        /// <summary>
        /// %AppData%/PhoenixControls/Hub/live-platforms.json — same roaming root
        /// as the other cross-session Hub state (dock layout, updater results),
        /// so it survives the reinstall that wipes the application directory.
        /// Returns "" when %AppData% is unavailable: there is
        /// deliberately NO BaseDirectory fallback, because a file in the install
        /// tree is exactly what the updater has to be able to delete/replace.
        /// Running without the snapshot is a clean degrade to the old behaviour.
        /// </summary>
        private static string DefaultSnapshotPath()
        {
            try
            {
                string dir = Paths.RoamingAppData("Hub");
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "live-platforms.json");
                }
            }
            catch { /* No AppData (sandbox / CI) — run without cross-restart memory. */ }
            return "";
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero) return "clock-skewed";
            if (age.TotalSeconds < 90) return $"{age.TotalSeconds:F0}s";
            if (age.TotalMinutes < 90) return $"{age.TotalMinutes:F0}m";
            return $"{age.TotalHours:F1}h";
        }
    }
}
