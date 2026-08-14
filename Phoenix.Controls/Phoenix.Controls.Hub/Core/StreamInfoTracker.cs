using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Ambient stream-info tracker — the cached CURRENT stream title + game
    // (category) the Hub never had. Before this, title/game were pull-only,
    // per-script values (Twitch.GetStream → "Phoenix: Get User" globals), so the
    // tool tokens {game}/{title} had no resolver and rendered blank
    // (CustomCommands left GameProvider unwired; Scheduling un-advertised them).
    //
    // Four feeds, all already in-process:
    //   1. Pushed platform events — Twitch.StreamUpdate / StreamUpdateGameOnConnect
    //      (newly subscribed in WS.TwitchEvents), Kick.ChannelUpdate /
    //      Kick.StreamOnline, YouTube.BroadcastStarted / BroadcastUpdated — probed
    //      here from the raw payload (TryApplyEvent, wired in
    //      ScriptManager.ExecuteGenericEventAsync's pre-guard region).
    //   2. Setter write-through — twitch.update_channel / kick.set_title /
    //      kick.set_category / youtube.set_title know the new value at dispatch.
    //   3. Pull write-through — every own-channel Get-User fetch
    //      (twitch.get_stream / get_user / is_online) already returns
    //      phx_user_title/phx_user_game; the handlers hand them in for free.
    //   4. A lazy one-shot seed + go-live refresh (ScriptManager.SeedStreamInfoAsync)
    //      so the cache isn't empty until the first change.
    //
    // Multi-platform semantics: ONE current (title, game) pair, most-recent-write
    // wins per field. A solo streamer is unaffected; a multi-platform streamer's
    // last title/category change is what the tokens show — deliberate v1 simplicity.
    //
    // Values also push into the engine's ambient {stream.title}/{stream.game}
    // tokens (ScriptEngine.SetStreamInfo) so Architect graphs read the same cache
    // without a Twitch.GetStream round-trip — Architect-first parity both ways.
    internal static class StreamInfoTracker
    {
        private static readonly object Gate = new();
        private static string _title = "";
        private static string _game = "";

        // Event types whose probe has already been reported as a total miss —
        // see NoteProbeMissOnce. Concurrent because TryApplyEvent runs on the
        // event-dispatch path; same shape as ScriptManager's
        // _missingSbActionWarned one-time nag.
        private static readonly ConcurrentDictionary<string, byte> ProbeMissWarned =
            new(StringComparer.OrdinalIgnoreCase);

        // Cap on how many payload field names the one-time diagnostic prints. A
        // fat event (Twitch ships 30+ fields on some payloads) must not turn the
        // hint into a log wall — the first handful is what identifies the shape.
        private const int MaxReportedFields = 20;

        /// <summary>Current stream title, "" until known.</summary>
        internal static string CurrentTitle { get { lock (Gate) return _title; } }
        /// <summary>Current game/category NAME, "" until known.</summary>
        internal static string CurrentGame { get { lock (Gate) return _game; } }
        /// <summary>
        /// Both fields read under ONE lock. CurrentTitle and CurrentGame are two
        /// separate acquisitions, so a Set() landing between a caller's two reads
        /// renders one stale and one fresh value in the SAME message. Anything
        /// needing both values must take this — Scheduling's {game}/{title} render
        /// does (ScriptManager.ResolveSchedulingTokens).
        ///
        /// CustomCommands is NOT covered and its {game}/{title} pair still reads
        /// torn: that path installs two independent seams
        /// (CustomCommandsService.GameProvider / .TitleProvider) which the service
        /// invokes separately per token, so no accessor here can close it. Sealing
        /// it needs a service-contract change (a single pair-returning seam) —
        /// deliberately out of scope, stated so nobody reads this accessor as
        /// proof the whole tool surface is atomic.
        /// </summary>
        internal static (string Title, string Game) CurrentPair { get { lock (Gate) return (_title, _game); } }
        /// <summary>True while EITHER field is still unknown (the seed trigger —
        /// per-field, so e.g. a game learned from StreamUpdateGameOnConnect doesn't
        /// stop the seed from supplying the still-missing title).</summary>
        internal static bool NeedsSeed { get { lock (Gate) return _title.Length == 0 || _game.Length == 0; } }

        /// <summary>
        /// Updates the cache. Null/blank leaves a field unchanged (a category-only
        /// event must not clear the title and vice versa); a real change is logged
        /// at Debug and mirrored into the engine's ambient stream.* tokens. The
        /// engine mirror happens INSIDE the lock so a concurrent pair of writers
        /// can never leave the mirror on the older pair for good.
        /// </summary>
        internal static void Set(string? title, string? game, string source)
            => Apply(title, game, source, onlyIfEmpty: false);

        /// <summary>
        /// Fill-only variant for the SEED path: each field is written only while
        /// still unknown. A seed answer is a background "Phoenix: Get User" fetch
        /// that can land seconds late — it must never overwrite a fresher pushed
        /// value (StreamUpdate / ChannelUpdate / a go-live payload).
        /// </summary>
        internal static void SetIfEmpty(string? title, string? game, string source)
            => Apply(title, game, source, onlyIfEmpty: true);

        private static void Apply(string? title, string? game, string source, bool onlyIfEmpty)
        {
            string? t = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            string? g = string.IsNullOrWhiteSpace(game) ? null : game.Trim();
            if (t is null && g is null) return;
            bool changed;
            string curT, curG;
            lock (Gate)
            {
                if (onlyIfEmpty)
                {
                    if (_title.Length > 0) t = null;
                    if (_game.Length > 0) g = null;
                }
                changed = (t is not null && !string.Equals(_title, t, StringComparison.Ordinal))
                       || (g is not null && !string.Equals(_game, g, StringComparison.Ordinal));
                if (t is not null) _title = t;
                if (g is not null) _game = g;
                curT = _title;
                curG = _game;
                if (changed) ScriptEngine.SetStreamInfo(curT, curG);
            }
            if (!changed) return;
            GlobalLogger.Log(
                $"Stream info updated via {source}: title=\"{curT}\" game=\"{curG}\"",
                "StreamInfoTracker", LogLevel.Debug);
        }

        /// <summary>Test hook — clears the cache (and the engine mirror).</summary>
        internal static void ResetForTest()
        {
            lock (Gate)
            {
                _title = "";
                _game = "";
                ScriptEngine.SetStreamInfo("", "");
            }
            // The one-time probe-miss nag is process state too — without this a
            // test that asserts the diagnostic would depend on test order.
            ProbeMissWarned.Clear();
        }

        /// <summary>
        /// Probes the title/game-bearing platform events. Field names follow the
        /// shipped PlatformEventCatalog defs (Kick/YouTube) and the Streamer.bot
        /// docs schema (Twitch StreamUpdate: status + game.Name) — defensively,
        /// with fallbacks, since SB publishes no wire contract. Unknown events
        /// no-op; a missing field simply leaves that half unchanged.
        /// </summary>
        internal static void TryApplyEvent(string eventType, JsonElement data)
        {
            if (string.IsNullOrEmpty(eventType)) return;
            if (data.ValueKind is not (JsonValueKind.Object)) return;

            if (Eq(eventType, "Twitch.StreamUpdate"))
            {
                // New title = "status" (old is "oldStatus"); game is an object
                // { Id, Name, ... } per the docs schema — probe the trigger-var
                // spelling and a flat string as fallbacks.
                ApplyProbed(eventType, data,
                    Probe(data, "status"),
                    Probe(data, "game.Name", "game.name", "gameName", "game"));
            }
            else if (Eq(eventType, "Twitch.StreamUpdateGameOnConnect"))
            {
                // Game state pushed at SB connect time — same game shape.
                ApplyProbed(eventType, data,
                    Probe(data, "status"),
                    Probe(data, "game.Name", "game.name", "gameName", "game"));
            }
            else if (Eq(eventType, "Kick.ChannelUpdate"))
            {
                // Kick calls the stream title "status" on this event (see the
                // PlatformEventCatalog def); docs also show a flat "title".
                ApplyProbed(eventType, data,
                    Probe(data, "status", "title"),
                    Probe(data, "categoryName", "category.name"));
            }
            else if (Eq(eventType, StreamLifecycle.KickOnline))
            {
                ApplyProbed(eventType, data,
                    Probe(data, "title"),
                    Probe(data, "category.name", "categoryName"));
            }
            else if (Eq(eventType, StreamLifecycle.YouTubeOnline))
            {
                // YouTube has no category concept; the go-live payload carries the
                // title (schema undocumented — same probe the catalog def uses).
                // BroadcastUpdated is deliberately NOT probed: it also fires for
                // SCHEDULED (not-yet-live) broadcasts, whose title would clobber
                // the live stream's.
                ApplyProbed(eventType, data, Probe(data, "broadcast.title", "title"), null);
            }
        }

        /// <summary>
        /// Writes a probed pair, or — when EVERY candidate path missed on an event
        /// we DO recognise — leaves the cache alone and reports the shape once.
        /// </summary>
        private static void ApplyProbed(string eventType, JsonElement data, string? title, string? game)
        {
            if (title is null && game is null)
            {
                NoteProbeMissOnce(eventType, data);
                return;
            }
            Set(title, game, eventType);
        }

        /// <summary>
        /// One-time-per-event-type note naming the payload's ACTUAL top-level
        /// fields. The probe keys above are best-effort, docs-derived guesses —
        /// Streamer.bot publishes no wire contract — so a platform reshaping its
        /// payload would silently blank {game}/{title} with no trace at any log
        /// level. Printing the real field names turns a wrong key into a one-line
        /// fix from a single live capture. Communication tier and once per event
        /// type, never per event: this repeats on every StreamUpdate otherwise.
        /// (Same one-time-note shape as twitch.is_online's arbitrary-channel nag.)
        /// </summary>
        private static void NoteProbeMissOnce(string eventType, JsonElement data)
        {
            if (!ProbeMissWarned.TryAdd(eventType, 0)) return;
            var names = new List<string>();
            foreach (var prop in data.EnumerateObject())
            {
                if (names.Count == MaxReportedFields) { names.Add("…"); break; }
                names.Add(prop.Name);
            }
            GlobalLogger.Log(
                $"Stream info: {eventType} carried no title/category at any known key, so {{game}}/{{title}} " +
                $"keep their previous values. Payload fields: {(names.Count > 0 ? string.Join(", ", names) : "(none)")}. " +
                "(One-time note.)",
                "StreamInfoTracker", LogLevel.Communication);
        }

        private static bool Eq(string a, string b)
            => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        // Dotted-path string probe over the payload; first non-blank wins.
        private static string? Probe(JsonElement data, params string[] paths)
        {
            foreach (var path in paths)
            {
                JsonElement cur = data;
                bool ok = true;
                foreach (var part in path.Split('.'))
                {
                    if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next))
                        cur = next;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                if (cur.ValueKind == JsonValueKind.String)
                {
                    string? s = cur.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }
    }
}
