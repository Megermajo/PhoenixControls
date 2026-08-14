using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // CustomCommandsService — the Hub-runtime brain of the text/variable-only custom
    // chat-command tool (sibling of QuotesService / CountersService). Stateless design:
    // the only persisted state is the tool CONFIG (the command list), held in a volatile
    // whole-object field and mirrored to the SYSTEM "CustomCommands" table. There is NO
    // open data table — a custom command renders a response template and sends it, and
    // the {count}/{count.next} tokens CONSUME the Counters tool (via injected Func seams
    // that point at CountersService), so this service never owns a counter table.
    //
    // Everything hot-path-testable lives here: Find(trigger) (name + aliases), the
    // dual-bucket cooldown (per-user + channel-wide global, monotonic clock), and the
    // pure substitution engine (Render). ScriptManager.CustomCommands.cs supplies the
    // per-platform send and wires the counter/uptime/game/channel seams + RaiseScriptEvent.
    public sealed class CustomCommandsService
    {
        private readonly DB _db;

        public CustomCommandsService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static CustomCommandsService? _instance;
        private static readonly object _instanceGate = new();
        public static CustomCommandsService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new CustomCommandsService(DB.Instance);
            }
        }

        // ── Config (cached; the command list is the tool's only state) ──────
        private volatile CustomCommandsConfig _config = new();
        public CustomCommandsConfig Config => _config;

        /// <summary>Master gate — false makes the built-in chat commands and the
        /// Command.OnCustom event a total no-op.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seams (null-safe; wired in ScriptManager.CustomCommands.cs) ──
        /// <summary>Raises a Command.OnCustom script event (event.command/user/args).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>Reads a Counters-tool counter's current value ({count}). Wired to
        /// CountersService.Instance.GetAsync — this tool consumes Counters, never owns one.</summary>
        public Func<string, Task<long>>? CounterGet { get; set; }

        /// <summary>Increments a Counters-tool counter by one and returns the new value
        /// ({count.next}). Wired to CountersService.Instance.AddAsync(name, 1).</summary>
        public Func<string, Task<long>>? CounterNext { get; set; }

        /// <summary>Resolves {channel} (the broadcaster/channel name). Wired to
        /// AppConfig.BroadcasterUsername. Returns "" when unavailable.</summary>
        public Func<string>? ChannelProvider { get; set; }

        /// <summary>Resolves {uptime}. Wired to the Hub's cached broadcaster-live state
        /// (ScriptManager.ResolveBroadcasterUptime) — the same source {stream.uptime}
        /// uses; renders "" while offline. Never throws.</summary>
        public Func<string>? UptimeProvider { get; set; }

        /// <summary>Resolves {game}. Wired to the Hub's ambient StreamInfoTracker
        /// (StreamUpdate/ChannelUpdate events + setter/Get-User write-through + lazy
        /// seed). Renders "" until the cache learns the value. Never throws.</summary>
        public Func<string>? GameProvider { get; set; }

        /// <summary>Resolves {title} (the current stream title). Same ambient
        /// StreamInfoTracker source as {game}. Renders "" until known. Never throws.</summary>
        public Func<string>? TitleProvider { get; set; }

        /// <summary>Monotonic clock (elapsed ms) for the cooldown buckets. Overridable
        /// by tests (a fake clock); defaults to a process-wide Stopwatch — never wall
        /// clock, so an NTP step / DST change can't unblock a cooldown early.</summary>
        public Func<long>? ClockMs { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "CustomCommandsService", "ConfigChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static readonly Stopwatch _mono = Stopwatch.StartNew();
        private long NowMs() => ClockMs?.Invoke() ?? _mono.ElapsedMilliseconds;

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadCustomCommandsConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<CustomCommandsConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CustomCommandsService", "InitializeAsync: config load failed", ex);
            }
            GlobalLogger.Log(
                $"CustomCommandsService online — {_config.Commands.Count} command(s), tool {(Active ? "ENABLED" : "disabled")}.",
                "CustomCommandsService", LogLevel.System);
        }

        private static CustomCommandsConfig Normalize(CustomCommandsConfig cfg)
        {
            cfg.Commands ??= new List<CustomCommand>();
            foreach (var c in cfg.Commands)
            {
                c.Trigger ??= "";
                c.Response ??= "";
                c.CounterName ??= "";
                c.Aliases ??= new List<string>();
                c.Roles ??= CommandRoles.All();
            }
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, and notifies the UI. The
        /// incoming object is deep-CLONED (JSON round-trip) so the panel VM can't alias
        /// the hot-path config instance (the Automod/Counters/Quotes aliasing lesson).</summary>
        public async Task SaveConfigAsync(CustomCommandsConfig cfg)
        {
            _config = Normalize(Clone(cfg ?? new CustomCommandsConfig()));
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveCustomCommandsConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CustomCommandsService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
        }

        private static CustomCommandsConfig Clone(CustomCommandsConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new CustomCommandsConfig());
                return JsonSerializer.Deserialize<CustomCommandsConfig>(json) ?? new CustomCommandsConfig();
            }
            catch { return new CustomCommandsConfig(); }
        }

        // ── Command resolution ──────────────────────────────────────────────
        /// <summary>The command whose Trigger or any Alias matches (case-insensitive,
        /// whole-token), or null. A blank/whitespace token never matches.</summary>
        public CustomCommand? Find(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0) return null;
            var cmds = _config.Commands;
            if (cmds == null) return null;
            foreach (var c in cmds)
            {
                if (c == null) continue;
                if (Eq(c.Trigger, token)) return c;
                if (c.Aliases != null)
                    foreach (var a in c.Aliases)
                        if (Eq(a, token)) return c;
            }
            return null;
        }

        // Row trigger / alias vs a parsed chat token. Goes through the one shared
        // canonicalizer so a command saved as "!hello" still fires — the token
        // reaching Find() has had its '!' stripped by the parser, and before
        // ChatVerb the configured side kept its own, which made "!hello" a
        // provably dead command the panel still rendered as active. Aliases are
        // edited straight into the live config by the panel and never pass a
        // normalization step, which is why the fix belongs here and not at load.
        private static bool Eq(string? a, string? b) => ChatVerb.Matches(a, b);

        // ── Dual-bucket cooldown (per-user + channel-wide global) ───────────
        // Keyed by the command's canonical trigger (lowercased) so aliases share the
        // same buckets. EITHER hot bucket blocks; both are stamped on a clear pass.
        private readonly object _cdGate = new();
        private readonly Dictionary<string, long> _globalCdMs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _userCdMs = new(StringComparer.Ordinal);

        /// <summary>True = clear to run (and both buckets stamped); false = blocked by
        /// either the per-user or the channel-wide global bucket.</summary>
        internal bool CooldownOk(string trigger, string userNorm, int userCd, int globalCd)
        {
            if (userCd <= 0 && globalCd <= 0) return true;   // no cooldown configured
            string key = (trigger ?? "").Trim().ToLowerInvariant();
            string userKey = key + "\0" + (userNorm ?? "");
            long now = NowMs();
            lock (_cdGate)
            {
                if (globalCd > 0 && _globalCdMs.TryGetValue(key, out long ge) && now < ge) return false;
                if (userCd > 0 && _userCdMs.TryGetValue(userKey, out long ue) && now < ue) return false;
                if (globalCd > 0) _globalCdMs[key] = now + globalCd * 1000L;
                if (userCd > 0) _userCdMs[userKey] = now + userCd * 1000L;
                return true;
            }
        }

        // ── Built-in chat-command core (testable; ScriptManager supplies the send) ──
        /// <summary>
        /// Resolves an inbound <c>!&lt;trigger&gt;</c> (or an alias) against the command
        /// list: if the tool is enabled AND the command is enabled AND the chatter's
        /// role is allowed AND the dual-bucket cooldown is clear, the response template
        /// is rendered through the variable library and sent via <paramref name="replyAsync"/>,
        /// then Command.OnCustom is raised. Returns true whenever a custom command was
        /// RECOGNIZED (so the caller suppresses the author on_chat fan-out) — a
        /// role-denied or cooldown-blocked command is still consumed silently (mirrors
        /// the Counters/Quotes parser). DEFAULT-OFF IS A TOTAL NO-OP — returns false the
        /// instant the tool is disabled. Bot self-messages are already dropped upstream.
        /// </summary>
        public async Task<bool> TryHandleChatCommandAsync(ChatMessage msg, Func<string, Task> replyAsync)
        {
            if (!Active || msg is null) return false;
            replyAsync ??= static _ => Task.CompletedTask;

            var cfg = _config;
            if (cfg.Commands == null || cfg.Commands.Count == 0) return false;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return false;
            string args = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();

            var cmd = Find(token);
            if (cmd == null || !cmd.Enabled) return false;   // not our command → author scripts handle it

            // Role gate + dual cooldown: a denied/blocked command is still consumed
            // (returns true) but produces no reply — a custom command replaces the
            // legacy author on_chat for that trigger. Role first so a denied user never
            // consumes the cooldown budget.
            // Role check consults the User-Management group overlay (a group-granted
            // Mod/VIP/Sub passes like the platform rank, and the Regular tick resolves
            // off the same overlay; passthrough while dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            if (cmd.Roles == null || !cmd.Roles.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular))
                return true;

            string userNorm = (msg.Username ?? string.Empty).Trim().ToLowerInvariant();
            if (!CooldownOk(cmd.Trigger, userNorm, cmd.UserCooldownSeconds, cmd.GlobalCooldownSeconds))
                return true;

            string rendered;
            try { rendered = await RenderAsync(cmd.Response, msg, args, cmd.EffectiveCounterName).ConfigureAwait(false); }
            catch (Exception ex)
            {
                GlobalLogger.Error("CustomCommandsService", "RenderAsync failed", ex);
                rendered = string.Empty;
            }

            // A multi-line response (the editor allows Shift+Enter line breaks and stores
            // them verbatim) is delivered as one chat message PER non-empty line: a single
            // platform chat send can't carry a newline — Twitch/YouTube/Kick collapse or
            // reject it — so without the split every line after the first was silently
            // dropped. A single-line response yields exactly one send (unchanged).
            foreach (var line in SplitResponseLines(rendered))
                await replyAsync(line).ConfigureAwait(false);

            // Reached only past the role gate, the dual cooldown and the sends above, so a
            // row here means the command genuinely answered someone.
            RecordActivity("CMD", $"!{ClipForActivity(cmd.Trigger)} answered {ClipForActivity(msg.Username)}.");
            RaiseCommandOnCustom(cmd.Trigger, msg.Username ?? "", args);
            return true;
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "CustomCommands";

        // A chat display name is viewer-supplied and a trigger is streamer-supplied; both
        // are unbounded, so both go through here. The command ARGUMENTS are deliberately
        // not recorded: they are raw viewer text, and the row exists to say which command
        // ran, not to quote chat back at the streamer.
        private const int ActivityFieldMaxChars = 40;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return "someone";
            return t.Length <= ActivityFieldMaxChars ? t : t[..ActivityFieldMaxChars].TrimEnd() + "...";
        }

        // Observation only: recording sits at the end of the chat-command path, so a fault
        // in it must never become a fault in the reply.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("CustomCommandsService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says.
        ///
        /// <para><see cref="NothingConfigured"/> is the state the master switch cannot say:
        /// the tool is on, and every command in it is disabled or has no trigger, so no
        /// chat line can ever match.</para>
        ///
        /// <para><b>There is deliberately NO "degraded because Counters is off" state.</b>
        /// The <c>{count}</c> / <c>{count.next}</c> seams are wired to
        /// <c>CountersService.GetAsync</c> / <c>AddAsync</c> (ScriptManager.CustomCommands.cs),
        /// and NEITHER is gated on the Counters master toggle — <c>GetAsync</c> is a bare
        /// <c>_db.CounterGetAsync</c>, and <c>AddAsync</c> writes first and only then checks
        /// <c>Active</c>, which gates the overlay publish and the <c>Counter.OnChanged</c>
        /// event alone (CountersService documents exactly this: "the Architect Counter.*
        /// nodes still perform the raw DB read/write regardless … only the reactive
        /// side-effects go dormant"). A response reading <c>{count}</c> therefore renders the
        /// real number with Counters switched off, and a pill claiming otherwise would be
        /// telling the streamer a lie about their own chat output.</para>
        /// </summary>
        public enum CustomCommandsPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>On, but no enabled command exists to answer anything.</summary>
            NothingConfigured,
            /// <summary>On, with commands.</summary>
            Live,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static CustomCommandsPillState ComputePillState(bool enabled, int enabledCommandCount)
        {
            if (!enabled) return CustomCommandsPillState.Dormant;
            if (enabledCommandCount == 0) return CustomCommandsPillState.NothingConfigured;
            return CustomCommandsPillState.Live;
        }

        /// <summary>The live pill state.</summary>
        public CustomCommandsPillState PillState
            => ComputePillState(_config.Enabled, EnabledCommandCount(_config));

        /// <summary>Commands that could answer: enabled, with a non-blank trigger — the same
        /// two conditions <c>Find</c> + the enabled check apply before anything is
        /// rendered.</summary>
        internal static int EnabledCommandCount(CustomCommandsConfig cfg)
        {
            if (cfg?.Commands is null) return 0;
            int n = 0;
            foreach (var c in cfg.Commands)
                if (c is not null && c.Enabled && !string.IsNullOrWhiteSpace(c.Trigger)) n++;
            return n;
        }

        /// <summary>Splits a rendered response into per-line chat messages: break on
        /// CR/LF, trim each line, drop blank lines. A single-line response is delivered
        /// VERBATIM (no trim) — the exact pre-multiline behavior — so only genuinely
        /// multi-line responses change. An empty response yields nothing.</summary>
        internal static IEnumerable<string> SplitResponseLines(string rendered)
        {
            if (string.IsNullOrEmpty(rendered)) yield break;
            // Single-line fast path — verbatim, exactly as the old single replyAsync did.
            if (rendered.IndexOf('\n') < 0 && rendered.IndexOf('\r') < 0)
            {
                yield return rendered;
                yield break;
            }
            foreach (var raw in rendered.Split('\n'))
            {
                string line = raw.Replace("\r", string.Empty).Trim();
                if (line.Length > 0) yield return line;
            }
        }

        private void RaiseCommandOnCustom(string command, string user, string args)
        {
            var raise = RaiseScriptEvent;
            if (raise == null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.command"] = command ?? "",
                    ["event.user"]    = user ?? "",
                    ["event.args"]    = args ?? "",
                };
                raise("Command.OnCustom", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("CustomCommandsService", "RaiseScriptEvent(Command.OnCustom) failed", ex);
            }
        }

        // ── Variable substitution engine (pure + unit-testable) ─────────────
        // Case-insensitive {token} matching. Unknown tokens are left VERBATIM. The two
        // counter tokens are async (they hit the Counters tool via the injected seams);
        // they are pre-resolved ONCE per render (so {count.next} increments exactly once
        // even if the token appears multiple times) and cached for the sync replace pass.
        private static readonly Regex TokenRx = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Renders <paramref name="template"/> against the chat context + variable
        /// library. <paramref name="args"/> is the text after the command word;
        /// <paramref name="counterName"/> is the counter {count}/{count.next} target.
        /// Never throws for a missing seam — an unwired {count}/{uptime}/{game} renders
        /// blank; a truly-unknown token is left verbatim.
        /// </summary>
        public async Task<string> RenderAsync(string template, ChatMessage msg, string args, string counterName)
        {
            if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
            msg ??= new ChatMessage();
            args ??= string.Empty;

            // {target}: first arg word, '@' stripped; else the chatter.
            string target = FirstArgTarget(args, msg.Username ?? string.Empty);

            // Pre-resolve the async counter tokens once, only when referenced.
            bool wantsCount = false, wantsNext = false;
            foreach (Match m in TokenRx.Matches(template))
            {
                string t = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (t == "count") wantsCount = true;
                else if (t == "count.next") wantsNext = true;
            }
            long? countVal = null, countNextVal = null;
            // Resolve the READ ({count}) BEFORE the increment ({count.next}) so a template
            // using both (e.g. "was {count}, now {count.next}") shows the pre-increment
            // value for {count} and the post-increment value for {count.next}.
            if (wantsCount)
            {
                var get = CounterGet;
                if (get != null)
                {
                    try { countVal = await get(counterName ?? "").ConfigureAwait(false); }
                    catch (Exception ex) { GlobalLogger.Error("CustomCommandsService", "CounterGet seam failed", ex); }
                }
            }
            if (wantsNext)
            {
                var next = CounterNext;
                if (next != null)
                {
                    try { countNextVal = await next(counterName ?? "").ConfigureAwait(false); }
                    catch (Exception ex) { GlobalLogger.Error("CustomCommandsService", "CounterNext seam failed", ex); }
                }
            }

            return TokenRx.Replace(template, m =>
                ResolveToken(m.Value, m.Groups[1].Value, msg, args, target, countVal, countNextVal));
        }

        private string ResolveToken(string original, string inner, ChatMessage msg, string args, string target,
                                    long? countVal, long? countNextVal)
        {
            string key = (inner ?? string.Empty).Trim().ToLowerInvariant();
            switch (key)
            {
                case "user":       return msg.Username ?? "";
                case "args":       return args ?? "";
                case "target":     return target ?? "";
                case "channel":    return SafeSeam(ChannelProvider);
                case "uptime":     return SafeSeam(UptimeProvider);
                case "game":       return SafeSeam(GameProvider);
                case "title":      return SafeSeam(TitleProvider);
                case "count":      return countVal.HasValue ? countVal.Value.ToString(CultureInfo.InvariantCulture) : "";
                case "count.next": return countNextVal.HasValue ? countNextVal.Value.ToString(CultureInfo.InvariantCulture) : "";
                case "random":     return RandomInRange(1, 100).ToString(CultureInfo.InvariantCulture);
            }

            // {random.N} → 1..N, {random.A-B} → A..B (inclusive). Unparseable → verbatim.
            if (key.StartsWith("random.", StringComparison.Ordinal))
            {
                string spec = key.Substring("random.".Length);
                // Search for the DELIMITER hyphen (skip a leading '-' of a negative lower
                // bound) so {random.-3-5} parses as [-3, 5] instead of leaking verbatim.
                int dash = spec.IndexOf('-', spec.StartsWith("-", StringComparison.Ordinal) ? 1 : 0);
                if (dash > 0)
                {
                    if (int.TryParse(spec.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo) &&
                        int.TryParse(spec.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi))
                        return RandomInRange(lo, hi).ToString(CultureInfo.InvariantCulture);
                }
                else if (int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    return RandomInRange(1, n).ToString(CultureInfo.InvariantCulture);
                }
            }

            return original;   // unknown token — leave verbatim
        }

        private static string SafeSeam(Func<string>? seam)
        {
            if (seam == null) return "";
            try { return seam() ?? ""; }
            catch { return ""; }
        }

        // Inclusive [lo, hi]; tolerant of a reversed range. Uses the shared thread-safe RNG.
        private static int RandomInRange(int lo, int hi)
        {
            if (hi < lo) (lo, hi) = (hi, lo);
            // 64-bit span so the inclusive top (including int.MaxValue) is reachable
            // without an int overflow on hi + 1.
            return (int)(lo + Random.Shared.NextInt64(0, (long)hi - lo + 1));
        }

        // {target}: the first whitespace-delimited word of args with a leading '@'
        // stripped; falls back to the chatter's own name when args is empty.
        internal static string FirstArgTarget(string args, string chatter)
        {
            args = (args ?? string.Empty).Trim();
            if (args.Length == 0) return chatter ?? "";
            int sp = args.IndexOf(' ');
            string first = (sp < 0 ? args : args.Substring(0, sp)).Trim();
            if (first.StartsWith("@", StringComparison.Ordinal)) first = first.Substring(1);
            return first.Length == 0 ? (chatter ?? "") : first;
        }
    }
}
