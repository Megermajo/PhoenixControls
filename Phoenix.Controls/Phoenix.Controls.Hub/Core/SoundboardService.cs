using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // SoundboardService — the Hub-runtime brain of the clip-playback tool (sibling of
    // CustomCommandsService / CountersService). Stateless apart from the tool CONFIG (the
    // row list + the overlay hookup), held in a volatile whole-object field and mirrored
    // to the SYSTEM "SoundboardConfig" table. No open data table: the clips are files in
    // data/media and the rows are configuration.
    //
    // WHAT IT ACTUALLY DOES, and what it deliberately does not:
    //
    //   • It resolves "!<word>" to a row, gates it on roles + a dual-bucket cooldown, and
    //     fires ONE shared visual trigger with {Args1="PLAY", Args2=<user>, Args3=<clip>}.
    //     The widget graph on the other end is Visual.Arg("Args3") → Audio.Load(Path) →
    //     Audio.Play, which is ordinary authored content: nothing here is a private
    //     channel into the browser.
    //   • It NEVER sends chat. A soundboard that narrates itself is noise, and the tool
    //     therefore carries no reply seam at all rather than an unused one.
    //   • It NEVER touches audio.play_tts. TTS is a Hub-LOCAL script command that speaks
    //     on the streamer's own machine through AudioService; this tool's audio is browser
    //     audio in OBS. They share the word "audio" and nothing else.
    //   • It registers NO script command, for the same reason ScriptManager.Alerts.cs
    //     does not: firing a visual trigger with an Args payload is something every
    //     Architect graph can already do (Visual.Trigger → visual.trigger_queued), so the
    //     tool is a no-code shortcut over existing surface and Architect-first parity
    //     holds with zero new engine surface.
    //   • It DOES raise one event root, Soundboard.OnPlay, and that is a parity fix rather
    //     than surface for its own sake: the built-in provider returns HandledSuppress, so
    //     mapping !airhorn here also switches OFF the author's on_chat handler for
    //     !airhorn — first-handled-wins, silently. Without an event root that graph goes
    //     dark with nothing to replace it. See RaisePlayed for what the raise binds and
    //     what it deliberately does not fire for.
    //
    // ★ THE ONE HAZARD THAT IS NOT VISIBLE FROM ANYWHERE ELSE — clip-path provenance.
    // Args3 lands on a WIRED Audio.Load.Path, and the compositor requires a wired media
    // path to be RELATIVE (see Evaluator._evalMediaPathSocket / isNonRelativeMediaPath).
    // It refuses a leading '/', an http(s): URL and a data: URI, loudly, once per
    // (node, value). It does NOT refuse a Windows absolute path, because "C:\x.mp3"
    // matches none of those three shapes: that one sails through resolveMediaPath, gets
    // URL-encoded into /media/C%3A%5Cx.mp3, and 404s — silence in OBS with nothing in the
    // log. So the tool validates the path HERE, at its own boundary, where a bad row can
    // still be named to the streamer. TryValidateClipPath is the single definition and
    // the panel uses it too, so the warning the streamer sees while authoring is the same
    // rule that gates the fire.
    //
    // ★ WHAT THAT BOUNDARY ACTUALLY GUARANTEES — stated exactly, because a hand-enumerated
    // list of bad prefixes got it wrong once already. The validator NORMALIZES first, and
    // normalization now strips a surrounding quote pair: Windows 11's Explorer context
    // menu ("Copy as path") copies "C:\sounds\airhorn.mp3" WITH the quotes, so the single
    // most likely way a streamer produces a clip value used to defeat every prefix test at
    // once — index 0 was a quote, not a drive letter — and the row rendered as correctly
    // configured right up to the silent 404. It then refuses, by ONE rule rather than six,
    // anything carrying a URI-scheme prefix (^[A-Za-z][A-Za-z0-9+.-]*:). A drive letter IS
    // such a prefix, so C:, file:, http:, https:, data: and every scheme nobody has thought
    // of yet fall to the same test instead of to a list that can be out-enumerated. On top
    // of that: the rooted forms ('/' and '//') and any '..' segment.
    //
    // What the boundary does NOT promise is that the file EXISTS. That is a separate,
    // best-effort probe (ClipIsInLibrary) with a separate failure mode — a shape error is
    // a refusal, a missing file is advice — and the two are never merged into one answer.
    public sealed class SoundboardService
    {
        private readonly DB _db;

        public SoundboardService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static SoundboardService? _instance;
        private static readonly object _instanceGate = new();
        public static SoundboardService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new SoundboardService(DB.Instance);
            }
        }

        // ── Config (cached; the row list is the tool's only state) ──────────
        private volatile SoundboardConfig _config = new();
        public SoundboardConfig Config => _config;

        /// <summary>Master gate — false makes the built-in chat provider a total
        /// no-op.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seam (null-safe; wired in ScriptManager.Soundboard.cs) ──
        /// <summary>Fire a visual trigger on every widget of the layer owning the trigger.
        /// Args: (layerId, triggerName, eventData). Wired to
        /// ScriptManager.FireVisualTriggerFanOutAsync, which logs its own "no widget owns
        /// this trigger" miss — see TryPrepareFire for why that is not enough on its
        /// own.</summary>
        public Func<string, string, Dictionary<string, string>, Task>? FireVisual { get; set; }

        /// <summary>Raises a <c>Soundboard.OnPlay</c> script event (wired in
        /// ScriptManager.Soundboard.cs). Same shape as RanksService.RaiseScriptEvent — an
        /// Action, because the service cannot await the generic-event dispatch; the seam
        /// owner does the fire-and-forget through AsyncErrorBoundary.</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>Monotonic clock (elapsed ms) for the cooldown buckets. Overridable by
        /// tests (a fake clock); defaults to a process-wide Stopwatch — never wall clock,
        /// so an NTP step / DST change can't unblock a cooldown early.</summary>
        public Func<long>? ClockMs { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        private void RaiseConfigChanged()
            => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "SoundboardService", "ConfigChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static readonly Stopwatch _mono = Stopwatch.StartNew();
        private long NowMs() => ClockMs?.Invoke() ?? _mono.ElapsedMilliseconds;

        /// <summary>The Args1 label every soundboard fire carries. Fixed rather than
        /// configurable: it exists so ONE widget graph can branch on "what kind of thing
        /// is this" the way the Alerts families do, and a graph that has been built
        /// against "PLAY" must keep matching after a config edit somewhere else.</summary>
        public const string PlayKind = "PLAY";

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadSoundboardConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<SoundboardConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardService", "InitializeAsync: config load failed", ex);
            }
            GlobalLogger.Log(
                $"SoundboardService online — {_config.Sounds.Count} sound(s), tool {(Active ? "ENABLED" : "disabled")}.",
                "SoundboardService", LogLevel.System);
        }

        private static SoundboardConfig Normalize(SoundboardConfig cfg)
        {
            cfg.LayerId ??= "";
            cfg.TriggerName ??= "";
            cfg.Sounds ??= new List<SoundDef>();
            foreach (var s in cfg.Sounds)
            {
                if (s is null) continue;
                s.Command ??= "";
                // Normalise on the way IN as well as on the way out: a blob written by a
                // hand edit (or an older build) can carry a backslashed path, and the
                // panel's warning must reflect the same string the fire path will use.
                s.ClipPath = NormalizeClipPath(s.ClipPath);
                s.Aliases ??= new List<string>();
                s.Roles ??= SoundboardRoles.All();
            }
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, and notifies the UI. The
        /// incoming object is deep-CLONED (JSON round-trip) so the panel VM can't alias
        /// the hot-path config instance (the Automod/Counters/Quotes aliasing
        /// lesson).</summary>
        public async Task SaveConfigAsync(SoundboardConfig cfg)
        {
            _config = Normalize(Clone(cfg ?? new SoundboardConfig()));
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveSoundboardConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
        }

        private static SoundboardConfig Clone(SoundboardConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new SoundboardConfig());
                return JsonSerializer.Deserialize<SoundboardConfig>(json) ?? new SoundboardConfig();
            }
            catch { return new SoundboardConfig(); }
        }

        // ── Clip-path provenance (the tool boundary; see the file header) ───

        /// <summary>
        /// Canonical form of a clip path: trimmed, stripped of a surrounding quote pair,
        /// backslashes folded to forward slashes, and any leading <c>./</c> segments
        /// dropped. Purely textual — it never touches the disk, so it is safe to call from
        /// a property setter on the UI thread.
        /// <para>★ The quote strip is the load-bearing part, not a tidy-up. Windows 11's
        /// Explorer context menu ("Copy as path") puts the path on the clipboard WRAPPED IN
        /// DOUBLE QUOTES, so the most likely way a streamer produces a clip value produces
        /// <c>"C:\sounds\airhorn.mp3"</c> — whose first character is a quote, not a drive
        /// letter. Unstripped it defeats every prefix test in
        /// <see cref="TryValidateClipPath"/> at once: the row validates, renders clean,
        /// fires, URL-encodes into <c>/media/%22C%3A/…</c> and 404s in silence.</para>
        /// <para>Folding the backslash keeps the stored value in the one form the rest of
        /// the pipeline speaks — the media API's <c>rel</c> field, the picker list, the
        /// <c>..</c> segment scan. It is no longer what makes the drive case detectable:
        /// the scheme rule matches <c>C:</c> whichever separator follows it.</para>
        /// </summary>
        public static string NormalizeClipPath(string? raw)
        {
            string s = StripSurroundingQuotes((raw ?? string.Empty).Trim()).Replace('\\', '/');
            while (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
            return s;
        }

        /// <summary>Drops matched pairs of surrounding <c>"</c> / <c>'</c>, re-trimming
        /// between passes so <c>' "C:\x.mp3" '</c> unwraps as readily as the single pair
        /// "Copy as path" produces.</summary>
        private static string StripSurroundingQuotes(string s)
        {
            while (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        /// <summary>
        /// ONE rule for what may travel to a wired <c>Audio.Load.Path</c>: a URI-scheme
        /// prefix is refused, and so are the rooted forms and traversal. Returns true and
        /// the normalized path when the value is a plain relative path under
        /// <c>data/media</c>; false plus a streamer-readable <paramref name="reason"/>
        /// otherwise. The reason is phrased to read after "This clip …" (the panel) and
        /// "its clip …" (the fire-path log), because those are its only two consumers.
        ///
        /// <para>Rejected, and why each one is here rather than left to the browser:</para>
        /// <list type="bullet">
        /// <item><b>blank</b> — nothing to play; the row would consume its word and do
        /// nothing.</item>
        /// <item><b>anything matching <c>^[A-Za-z][A-Za-z0-9+.-]*:</c></b> — one test
        /// covering the whole family. A Windows drive letter (<c>C:/…</c>) is the SILENT
        /// member: the compositor's non-relative test names <c>/</c>, http(s) and data and
        /// nothing else, so a drive path is URL-encoded into <c>/media/C%3A%2F…</c>, 404s,
        /// and produces no diagnostic anywhere. <c>file:</c> behaved identically until this
        /// became one rule, which is exactly why enumerating schemes by hand was the wrong
        /// shape: the list can always be out-enumerated, the rule cannot.</item>
        /// <item><b>leading '/' or '//'</b> — rejected by the compositor with a
        /// diagnostic, but rejecting it here means the streamer learns while editing
        /// rather than mid-stream.</item>
        /// <item><b>a <c>..</c> segment</b> — HUDServer's ServeFileFromRootAsync would
        /// 403 the traversal, so this is not a security fix; it is a diagnostic one, since
        /// a 403 reads identically to a missing file from the streamer's chair.</item>
        /// </list>
        ///
        /// <para>What it does NOT check is EXISTENCE — see
        /// <see cref="ClipIsInLibrary"/>. Shape is a refusal; a missing file is advice.</para>
        /// </summary>
        public static bool TryValidateClipPath(string? raw, out string normalized, out string reason)
        {
            normalized = NormalizeClipPath(raw);
            if (normalized.Length == 0)
            {
                reason = "is not set";
                return false;
            }
            if (normalized.StartsWith("//", StringComparison.Ordinal))
            {
                reason = "is a network share, which the overlay cannot fetch — copy the file into your media library instead";
                return false;
            }
            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                reason = "starts with '/', which the overlay refuses — make it relative to your media library";
                return false;
            }
            // A double quote SURVIVING normalization means the pair was unmatched — a stray
            // leading quote from a half-selected "Copy as path", say. Stripping matched pairs is
            // not enough on its own: `"C:/x.mp3` keeps its quote at index 0, so the scheme test
            // below (anchored on a LETTER) misses it, the row validates clean, and the fire
            // 404s in silence — the same hole the matched-pair strip was added to close, one
            // keystroke away. Windows reserves '"' in filenames, so a path containing one can
            // never name a real file and there is nothing legitimate to preserve here.
            if (normalized.IndexOf('"') >= 0)
            {
                reason = "contains a quote character, which no filename can — paste the path without "
                       + "the surrounding quotes, or pick the clip from your media library";
                return false;
            }
            if (UriSchemePrefixRegex.IsMatch(normalized))
            {
                reason = HasDrivePrefix(normalized)
                    ? "is a full drive path, which never reaches the overlay — pick the clip from your media library so the path is relative (e.g. audio/airhorn.mp3)"
                    : $"is a '{normalized.Substring(0, normalized.IndexOf(':'))}:' address, and only files from your media library can be played — copy the file in and pick it here";
                return false;
            }
            foreach (var segment in normalized.Split('/'))
            {
                if (segment == "..")
                {
                    reason = "steps outside your media library — remove the '..'";
                    return false;
                }
            }
            reason = "";
            return true;
        }

        /// <summary>
        /// The single non-relative test: any URI-scheme prefix, of which a Windows drive
        /// letter is one (<c>C:</c> matches <c>&lt;scheme&gt;:</c> exactly). It replaced
        /// four hand-written prefix comparisons — drive, http, https, data — which between
        /// them let <c>file:///C:/x.mp3</c> through untouched.
        /// <para>Anchored and backtrack-free over a path-length string, so the
        /// per-keystroke cost the hand-rolled drive test was written to avoid is a few
        /// hundred nanoseconds. Correctness over that margin: the hand-rolled version is
        /// what the quoted-path hole was hiding behind.</para>
        /// </summary>
        private static readonly Regex UriSchemePrefixRegex =
            new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>True for "C:/…" / "c:" — a Windows drive-qualified path AFTER
        /// <see cref="NormalizeClipPath"/> has run. No longer a gate: the scheme rule above
        /// already refused the value, and this only picks which sentence the streamer
        /// reads, since "a full drive path" is far more actionable than "a 'C:' address".</summary>
        private static bool HasDrivePrefix(string s)
            => s.Length >= 2 && s[1] == ':' &&
               ((s[0] >= 'A' && s[0] <= 'Z') || (s[0] >= 'a' && s[0] <= 'z'));

        // ── Clip library (the picker's item source) ─────────────────────────
        /// <summary>
        /// Every audio file in the media library, as the same forward-slashed relative
        /// path <c>GET /api/media</c> reports in its <c>rel</c> field — i.e. already in
        /// the only form <see cref="TryValidateClipPath"/> accepts. Sorted, empty (never
        /// throwing) when the library does not exist yet.
        ///
        /// <para>It reads <see cref="HUDServer.ResolveMediaRoot"/> and classifies with
        /// <see cref="HUDServer.MediaKindForExtension"/> deliberately: a picker that
        /// enumerated a different folder, or accepted a different extension set, than the
        /// server that later serves the file would offer the streamer clips that play
        /// nowhere.</para>
        /// </summary>
        public static IReadOnlyList<string> EnumerateClipCandidates()
        {
            var result = new List<string>();
            string root;
            try { root = HUDServer.ResolveMediaRoot(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardService", "media root resolution failed", ex);
                return result;
            }
            try
            {
                if (!Directory.Exists(root)) return result;
                string rootFull = Path.GetFullPath(root);
                foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
                {
                    if (HUDServer.MediaKindForExtension(Path.GetExtension(file)) != "audio") continue;
                    result.Add(Path.GetRelativePath(rootFull, file).Replace('\\', '/'));
                }
            }
            catch (Exception ex)
            {
                // A vanished folder / denied subtree must not blank the picker: keep
                // whatever was enumerated before the fault and say so.
                GlobalLogger.Error("SoundboardService", "clip enumeration failed", ex);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Best-effort "is this clip actually there" probe, over the SAME (media root,
        /// audio-extension) pair <see cref="EnumerateClipCandidates"/> enumerates — so the
        /// picker, the row warning and this answer can never disagree about what counts as
        /// a clip.
        ///
        /// <para>Why it exists: shape validation cannot see the commonest reason a mapped
        /// clip goes quiet. Rename the file, delete it, or mistype one character, and the
        /// path is still perfectly well-formed — it just 404s. Without this the streamer
        /// gets a fire, silence, and nothing in the log.</para>
        ///
        /// <para>Returns <c>null</c> for "cannot tell": the value is not even shape-valid
        /// (a different diagnostic owns that), the media root did not resolve, the library
        /// does not exist yet, or the probe threw. A probe that could not run must never
        /// invent a miss — a false "your clip is gone" is worse than no warning.</para>
        /// </summary>
        public static bool? ClipIsInLibrary(string? clipPath)
        {
            if (!TryValidateClipPath(clipPath, out string rel, out _)) return null;
            try
            {
                // Not audio → the server would happily serve it and Audio.Load would play
                // nothing, so it is a miss rather than a "cannot tell".
                if (!string.Equals(HUDServer.MediaKindForExtension(Path.GetExtension(rel)), "audio",
                                   StringComparison.Ordinal))
                    return false;

                string root = HUDServer.ResolveMediaRoot();
                if (!Directory.Exists(root)) return null;
                // GetFullPath for the same reason EnumerateClipCandidates uses it: the
                // resolver can hand back a path relative to the process working directory,
                // and the two must resolve identically or the picker will offer a clip this
                // probe then reports missing.
                return File.Exists(Path.Combine(Path.GetFullPath(root), rel));
            }
            catch (Exception ex)
            {
                // Debug, not Error: a denied subtree on the fire path would otherwise
                // repeat once per sound played, and the answer degrades to "no opinion"
                // rather than to a wrong one.
                GlobalLogger.Log(
                    $"Soundboard: could not check whether '{rel}' is in the media library ({ex.Message}).",
                    "SoundboardService", LogLevel.Debug);
                return null;
            }
        }

        // ── Row resolution ──────────────────────────────────────────────────
        /// <summary>
        /// The row whose Command or any Alias matches (case-insensitive, whole-token), or
        /// null. A blank/whitespace token never matches.
        ///
        /// <para>★ An ENABLED match wins over an earlier disabled one. Two rows can share a
        /// word — nothing stops a streamer duplicating a row to retire the old version —
        /// and a plain first-match returned the disabled one, which
        /// <see cref="TryHandleChatCommandAsync"/> then treats as "not ours". The word fell
        /// through to Custom Commands and the live row it was duplicated FROM never fired,
        /// with nothing said anywhere. Falling back to the first match when none is enabled
        /// keeps the disabled-row-falls-through behaviour intact for the ordinary case of a
        /// single switched-off row. The Soundboard page warns about the duplicate either
        /// way, and names the row that actually wins by this same rule.</para>
        /// </summary>
        public SoundDef? Find(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0) return null;
            var sounds = _config.Sounds;
            if (sounds == null) return null;
            SoundDef? firstAny = null;
            foreach (var s in sounds)
            {
                if (s == null) continue;
                if (!Claims(s, token)) continue;
                if (s.Enabled) return s;
                firstAny ??= s;
            }
            return firstAny;
        }

        /// <summary>True when the row answers to <paramref name="token"/> — its command or
        /// any of its aliases.</summary>
        private static bool Claims(SoundDef s, string token)
        {
            if (Eq(s.Command, token)) return true;
            if (s.Aliases != null)
                foreach (var a in s.Aliases)
                    if (Eq(a, token)) return true;
            return false;
        }

        // Row word / alias vs a parsed chat token. Goes through the one shared
        // canonicalizer so a row (or an alias) saved as "!airhorn" still fires —
        // the token reaching Find() has had its '!' stripped by the parser, and
        // before ChatVerb the configured side kept its own. Aliases are edited
        // straight into the live config by the panel and never pass a
        // normalization step, which is why the fix belongs here and not at load.
        private static bool Eq(string? a, string? b) => ChatVerb.Matches(a, b);

        // ── Dual-bucket cooldown (per-user + channel-wide global) ───────────
        // Keyed by the row's canonical command (lowercased) so aliases share the same
        // buckets. EITHER hot bucket blocks; both are stamped on a clear pass. Byte-for-
        // byte the CustomCommandsService shape — a soundboard has no reason to invent a
        // second cooldown semantic for the streamer to learn.
        private readonly object _cdGate = new();
        private readonly Dictionary<string, long> _globalCdMs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _userCdMs = new(StringComparer.Ordinal);

        // ★ _userCdMs is BOUNDED, and the sibling services it was copied from are not.
        // Its key is command\0username, so it grows by one permanent entry per distinct
        // chatter per sound and never shrinks for the life of the process. On the tools
        // that shape came from that is a slow leak nobody meets; on a soundboard it is the
        // expected traffic — a soundboard is the tool a whole chat plays with, and a raid
        // brings thousands of one-off names through it in minutes.
        //
        // The bound is the AutomodService.MaybeSweepRate idiom (a counter-gated sweep, so
        // the cost is amortised and the hot path stays two dictionary probes) with
        // HUDServer's hard-ceiling backstop behind it (_webhookHits / PruneWebhookHitsLru):
        // the sweep drops entries whose cooldown has already expired, and if a genuinely
        // huge configured cooldown keeps them all live, the ceiling evicts the
        // soonest-to-expire — the entries closest to being free anyway.
        private const int CooldownSweepEvery = 128;
        private const int MaxUserCooldownEntries = 4096;
        private int _cdStampCounter;

        /// <summary>True = clear to play (and both buckets stamped); false = blocked by
        /// either the per-user or the channel-wide global bucket.
        /// <para>Called only once the fire is known to be possible — see
        /// <see cref="TryHandleChatCommandAsync"/> — so a stamp always corresponds to a
        /// real dispatch attempt rather than to a row that was never going to play.</para>
        /// </summary>
        internal bool CooldownOk(string command, string userNorm, int userCd, int globalCd)
        {
            if (userCd <= 0 && globalCd <= 0) return true;   // no cooldown configured
            string key = (command ?? "").Trim().ToLowerInvariant();
            string userKey = key + "\0" + (userNorm ?? "");
            long now = NowMs();
            lock (_cdGate)
            {
                if (globalCd > 0 && _globalCdMs.TryGetValue(key, out long ge) && now < ge) return false;
                if (userCd > 0 && _userCdMs.TryGetValue(userKey, out long ue) && now < ue) return false;
                if (globalCd > 0) _globalCdMs[key] = now + globalCd * 1000L;
                if (userCd > 0)
                {
                    _userCdMs[userKey] = now + userCd * 1000L;
                    MaybeSweepUserCooldownsUnlocked(now);
                }
                return true;
            }
        }

        /// <summary>Live per-user cooldown entry count — the bound's only observable
        /// surface. <c>internal</c> rather than public because the csproj already grants
        /// <c>InternalsVisibleTo("Phoenix.Controls.Tests")</c>, the same seam
        /// VarChainAnalyzer.DispatchKeysForTests uses.</summary>
        internal int UserCooldownEntryCountForTests
        {
            get { lock (_cdGate) return _userCdMs.Count; }
        }

        // Caller holds _cdGate. _globalCdMs is deliberately not swept: it is keyed by the
        // row's command alone, so it is bounded by the size of the board.
        private void MaybeSweepUserCooldownsUnlocked(long now)
        {
            if (++_cdStampCounter < CooldownSweepEvery) return;
            _cdStampCounter = 0;

            if (_userCdMs.Count > 64)
            {
                var expired = new List<string>();
                foreach (var kv in _userCdMs)
                    if (now >= kv.Value) expired.Add(kv.Key);
                foreach (var k in expired) _userCdMs.Remove(k);
            }

            if (_userCdMs.Count <= MaxUserCooldownEntries) return;

            // Every survivor is still hot (a multi-hour cooldown on a busy channel).
            // Evict soonest-expiring first, down to the ceiling: those chatters get their
            // sound back a little early, which is the mildest failure available here.
            var live = new List<KeyValuePair<string, long>>(_userCdMs);
            live.Sort((a, b) => a.Value.CompareTo(b.Value));
            int toRemove = live.Count - MaxUserCooldownEntries;
            for (int i = 0; i < toRemove; i++) _userCdMs.Remove(live[i].Key);
        }

        // ── Built-in chat-command core (testable; ScriptManager owns the seam) ──
        /// <summary>
        /// Resolves an inbound <c>!&lt;word&gt;</c> (or an alias) against the row list: if
        /// the tool is enabled AND the row is enabled AND the chatter's role is allowed
        /// AND the dual-bucket cooldown is clear, the clip is fired at the board's widget.
        /// Returns true whenever a soundboard row was RECOGNIZED (so the caller suppresses
        /// the author on_chat fan-out) — a role-denied, cooldown-blocked or
        /// bad-clip-path row is still consumed silently, mirroring the Counters / Quotes /
        /// CustomCommands parsers: the word belongs to the soundboard either way, and a
        /// tool that answered "you may not" in chat would be a spam vector. DEFAULT-OFF IS
        /// A TOTAL NO-OP — returns false the instant the tool is disabled. Bot
        /// self-messages are already dropped upstream.
        /// <para>A real play — and only a real play — additionally raises
        /// <c>Soundboard.OnPlay</c>, which is how an author graph observes a word this
        /// provider consumed out from under its on_chat handler. See
        /// <see cref="RaisePlayed"/>.</para>
        /// </summary>
        public async Task<bool> TryHandleChatCommandAsync(ChatMessage msg)
        {
            if (!Active || msg is null) return false;

            var cfg = _config;
            if (cfg.Sounds == null || cfg.Sounds.Count == 0) return false;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return false;

            // Find prefers an ENABLED row, so this only rejects when EVERY row claiming the
            // word is switched off — which is the per-row off switch doing its job, not one
            // stale row hiding a live duplicate behind it.
            var sound = Find(token);
            if (sound == null || !sound.Enabled) return false;   // not ours → later providers / author scripts

            // Role gate first, so a denied user never consumes the cooldown budget. The
            // check consults the User-Management group overlay (a group-granted Mod/VIP/Sub
            // passes like the platform rank, and the Regular tick resolves off the same
            // overlay; pure passthrough while that tool is dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            if (sound.Roles == null || !sound.Roles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular))
                return true;

            // ★ PRE-FLIGHT BEFORE THE COOLDOWN, and the order is the fix rather than a
            // tidy-up. The cooldown check IS the stamp (one atomic pass under _cdGate), so
            // stamping first meant a board with no overlay hookup, a refused clip path or
            // an unwired seam burned a full channel-wide lockout on every attempt at a
            // sound that could never play — the misconfigured case, locked out for the
            // configured window, with only the log line to say why. Everything that can
            // refuse the fire is knowable here, so it is decided here; what remains after
            // the stamp is the seam itself, which is a genuine dispatch attempt.
            if (!TryPrepareFire(cfg, sound, out string layerId, out string triggerName,
                                out string clip, out var fire))
                return true;   // recognised, refused, and named in the log by TryPrepareFire

            string userNorm = (msg.Username ?? string.Empty).Trim().ToLowerInvariant();
            if (!CooldownOk(sound.Command, userNorm, sound.UserCooldownSeconds, sound.GlobalCooldownSeconds))
                return true;

            await FireAsync(fire, layerId, triggerName, sound, clip, msg.Username ?? "").ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Resolves everything the fire needs and names every reason it cannot happen.
        ///
        /// <para>Every way this tool can fail to make a sound is LOGGED, and that is the
        /// whole point of this method existing separately. A soundboard's failure mode is
        /// silence, which is indistinguishable from "the streamer's speakers are muted"
        /// unless something says otherwise. Four distinct silences are named here — no
        /// overlay hookup, a clip path the overlay will refuse, a clip that is not in the
        /// media library, and an unwired seam — a fifth (a seam that threw) is named by
        /// <see cref="FireAsync"/>, and a sixth (a hookup naming a trigger no widget owns)
        /// by FireVisualTriggerFanOutAsync itself.</para>
        ///
        /// <para>The missing-clip case is the one that does NOT refuse: existence is a
        /// best-effort probe (see <see cref="ClipIsInLibrary"/>), and a probe that is only
        /// mostly right must not be able to mute a working board. It logs and lets the fire
        /// through, so the streamer gets the one sentence that explains the silence.</para>
        /// </summary>
        private bool TryPrepareFire(SoundboardConfig cfg, SoundDef sound,
                                    out string layerId, out string triggerName, out string clip,
                                    out Func<string, string, Dictionary<string, string>, Task> fire)
        {
            layerId = (cfg.LayerId ?? "").Trim();
            triggerName = (cfg.TriggerName ?? "").Trim();
            clip = "";
            fire = null!;

            if (layerId.Length == 0 || triggerName.Length == 0)
            {
                GlobalLogger.Log(
                    $"Soundboard: !{sound.Command} matched, but the board has no overlay layer/trigger set — nothing to play on. " +
                    "Set them on the Soundboard page.",
                    "SoundboardService", LogLevel.VisualEvent);
                RecordRefusal("DENY",$"!{ClipForActivity(sound.Command)} — no overlay layer/trigger is set, so nothing can play.");
                return false;
            }

            if (!TryValidateClipPath(sound.ClipPath, out clip, out string reason))
            {
                GlobalLogger.Log(
                    $"Soundboard: !{sound.Command} was not played — its clip {reason} (value: '{sound.ClipPath}').",
                    "SoundboardService", LogLevel.VisualEvent);
                RecordRefusal("DENY",$"!{ClipForActivity(sound.Command)} — clip {reason}.");
                return false;
            }

            var seam = FireVisual;
            if (seam == null)
            {
                GlobalLogger.Log(
                    $"Soundboard: !{sound.Command} matched, but the visual seam is unwired — no overlay fire is possible.",
                    "SoundboardService", LogLevel.VisualEvent);
                RecordRefusal("DENY",$"!{ClipForActivity(sound.Command)} — the visual seam is unwired, so no overlay fire is possible.");
                return false;
            }
            fire = seam;

            if (ClipIsInLibrary(clip) == false)
            {
                GlobalLogger.Log(
                    $"Soundboard: !{sound.Command} points at '{clip}', which is not in your media library — " +
                    "the overlay will fetch it and get a 404, so this will play nothing. Re-pick the clip on the Soundboard page.",
                    "SoundboardService", LogLevel.VisualEvent);
                // WARN, not DENY: this branch deliberately does NOT refuse — the existence
                // check is best-effort, so the fire goes through and a PLAY row follows.
                RecordRefusal("WARN", $"!{ClipForActivity(sound.Command)} points at '{ClipForActivity(clip)}', which is not in the media library — the overlay will get a 404.");
            }
            return true;
        }

        /// <summary>
        /// Fires one row at the board's widget: Args1="PLAY", Args2=&lt;who asked&gt;,
        /// Args3=&lt;clip path&gt;, then raises <c>Soundboard.OnPlay</c>.
        /// </summary>
        private async Task FireAsync(Func<string, string, Dictionary<string, string>, Task> fire,
                                     string layerId, string triggerName, SoundDef sound, string clip, string user)
        {
            var eventData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Args1"] = PlayKind,
                ["Args2"] = user ?? "",
                ["Args3"] = clip,
            };

            // AWAITED inside the try, not fired and forgotten: the seam returns a Task, so
            // dropping it would move any fault into an unobserved Task and turn a broken
            // board into silence with an empty log.
            try
            {
                await fire(layerId, triggerName, eventData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardService", $"visual fire for !{sound.Command} failed", ex);
                RecordRefusal("DENY",$"!{ClipForActivity(sound.Command)} — the overlay fire threw, so nothing played.");
                return;   // no clip left the building — do not tell graphs one did
            }

            // Same honesty rule as RaisePlayed, one line above it: a PLAY row exists only
            // for a dispatch that actually happened.
            RecordActivity("PLAY", $"!{ClipForActivity(sound.Command)} played for {ClipForActivity(user)}.");
            RaisePlayed(sound, clip, user ?? "");
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Soundboard";

        // A chat display name is viewer-supplied; a command word and a clip path are
        // streamer-supplied. All three are unbounded, so all three go through here.
        private const int ActivityFieldMaxChars = 48;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return "someone";
            return t.Length <= ActivityFieldMaxChars ? t : t[..ActivityFieldMaxChars].TrimEnd() + "...";
        }

        // Observation only: recording sits on the chat-command path, so a fault in it must
        // never become a fault in the board.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("SoundboardService", "activity record failed", ex); }
        }

        /// <summary>How long an IDENTICAL refusal is suppressed before it is recorded
        /// again.</summary>
        internal const long RefusalRepeatMs = 60_000L;

        private readonly object _refusalGate = new();
        private string _lastRefusalMessage = "";
        // 0, not long.MinValue: the elapsed subtraction below would overflow against
        // MinValue. It is only ever reached once a message has matched, and no real refusal
        // message is "", so the first call always takes the record branch regardless.
        private long _lastRefusalMs;

        // A refusal is recorded per ATTEMPT, and the refusals here are all standing
        // misconfigurations — a blank hookup refuses every row of every viewer, forever. A
        // viewer leaning on !airhorn would push every other row out of a bounded ring with
        // one repeated sentence, so an identical refusal is recorded at most once a minute.
        // The System Log line above is untouched: it is unbounded and stays the complete
        // record.
        private void RecordRefusal(string kind, string message)
        {
            lock (_refusalGate)
            {
                long now = NowMs();
                if (string.Equals(message, _lastRefusalMessage, StringComparison.Ordinal)
                    && now - _lastRefusalMs < RefusalRepeatMs)
                    return;
                _lastRefusalMessage = message;
                _lastRefusalMs = now;
            }
            RecordActivity(kind, message);
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says. <see cref="CannotPlay"/> is the killer
        /// misconfiguration and the reason this pill is worth its space: with a blank layer
        /// or trigger, <c>TryPrepareFire</c> refuses EVERY row, so an enabled board with a
        /// dozen sounds can never make a noise — and nothing on the page says so except one
        /// red line inside the overlay card.
        ///
        /// <para><see cref="OverlayNotRendering"/> is the softer sibling: the hookup is set,
        /// but the layer it names has no browser surface attached, so a fire dispatches into
        /// nothing.</para>
        /// </summary>
        public enum SoundboardPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>On, but the board has no layer and/or no trigger — every row is
            /// refused before it reaches the overlay.</summary>
            CannotPlay,
            /// <summary>On and configured, but the named layer is not currently
            /// rendering.</summary>
            OverlayNotRendering,
            /// <summary>On, configured, and the layer is live.</summary>
            Ready,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static SoundboardPillState ComputePillState(
            bool enabled, string? layerId, string? triggerName, bool layerActive)
        {
            if (!enabled) return SoundboardPillState.Dormant;
            // The exact pair TryPrepareFire tests, trimmed the same way.
            if (string.IsNullOrWhiteSpace(layerId) || string.IsNullOrWhiteSpace(triggerName))
                return SoundboardPillState.CannotPlay;
            if (!layerActive) return SoundboardPillState.OverlayNotRendering;
            return SoundboardPillState.Ready;
        }

        /// <summary>The live pill state.</summary>
        public SoundboardPillState PillState
        {
            get
            {
                var cfg = _config;
                string layerId = (cfg.LayerId ?? "").Trim();
                bool layerActive;
                try { layerActive = layerId.Length > 0 && LayerRegistry.Instance.IsLayerActive(layerId); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("SoundboardService", "layer presence read failed", ex);
                    // Unknown ⇒ do not accuse the overlay.
                    layerActive = true;
                }
                return ComputePillState(cfg.Enabled, layerId, cfg.TriggerName, layerActive);
            }
        }

        /// <summary>
        /// The <c>Soundboard.OnPlay</c> script event. The token set is pinned in three more
        /// places (the Soundboard.OnPlay arm in ScriptExporter.ResolveOutputFromNode,
        /// AutocompleteScopeBuilder and VarChainAnalyzer.ResultEmitterMap), so it is built
        /// here once.
        ///
        /// <para>★ WHY THE TOOL HAS AN EVENT ROOT AT ALL. The built-in provider returns
        /// HandledSuppress, which additionally skips the author on_chat fan-out — so
        /// mapping !airhorn on this board switches OFF an Architect graph that was handling
        /// !airhorn on on_chat, silently, first-handled-wins. Without a root to move that
        /// graph onto, the tool's arrival is a regression for anyone who had already built
        /// the thing by hand.</para>
        ///
        /// <para>It fires on a REAL dispatch only: not for a row a role gate refused, not
        /// for one a cooldown blocked, not for one whose clip the boundary rejected, and
        /// not when the seam threw. An "OnPlay" that fired when nothing played would be the
        /// same class of lie as the clip warning this sprint fixed. Those refusals are
        /// logged (see TryPrepareFire) rather than eventful.</para>
        /// </summary>
        private void RaisePlayed(SoundDef sound, string clip, string user)
        {
            var raise = RaiseScriptEvent;
            if (raise is null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.command"] = (sound.Command ?? "").Trim(),
                    ["event.user"] = user,
                    ["event.clip"] = clip,
                    // Bound for the same reason RanksService binds it: {user.name} is what a
                    // chat line reaches for, and a graph moved off on_chat should not have
                    // to relearn the token it was already using.
                    ["user.name"] = user,
                };
                raise("Soundboard.OnPlay", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("SoundboardService", "RaiseScriptEvent(Soundboard.OnPlay) failed", ex);
            }
        }
    }
}
