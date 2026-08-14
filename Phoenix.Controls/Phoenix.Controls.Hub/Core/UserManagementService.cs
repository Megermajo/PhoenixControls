using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // UserManagementService — the runtime for the User-Management pre-build tool.
    //
    // Three halves:
    //   WELCOMING — greets the FIRST chat message a user sends per stream. Runs as an
    //   observe-only built-in chat provider (never handles/suppresses — the same line
    //   may also be a command for a later provider). The welcomed-set is DB-persisted
    //   (UserMgmtSeen) so a mid-stream Hub restart does not re-greet returning
    //   chatters; it clears on the offline→live transition (SetStreamLive), which is
    //   the per-stream reset Majo chose.
    //
    //   GREETING — welcomes a brand-new chatter ONCE EVER, on their first message in
    //   the channel's lifetime. Backed by the never-cleared UserMgmtSeenEver set,
    //   which is RECORDED whenever the tool is enabled (baseline building) but only
    //   SENT from when GreetingEnabled is on. Precedence on the one first-ever
    //   message: personalized row > first-time greeting > general welcome.
    //
    //   GROUPS — the membership tiers every role check in Phoenix reads (script vars,
    //   node outputs, every built-in tool's role gate). Four standard groups stay
    //   PICKABLE everywhere a group is picked — Moderator / VIP / Subscriber / Regular —
    //   but only Regular and the custom groups keep a member list here. Moderator / VIP /
    //   Subscriber membership is the PLATFORM's answer: the role flags stamped on the
    //   viewer's own chat message where there is one (EffectiveRoles), and
    //   ViewerPresenceService's platform-role cache where there is not (LookupGroups).
    //   A second hand-maintained copy of a rank Twitch / YouTube / Kick already publishes
    //   could only ever disagree with them, and when it did it silently outranked them
    //   suite-wide. Regular and the custom groups additionally carry a WATCH-HOUR rule,
    //   computed at check time against the open "WatchTime" table and never written back
    //   into the member list — so lowering the threshold includes everybody at once and
    //   raising it takes the grant back without rewriting a single row.
    //
    //   VIEWER QUEUE — a line viewers join from chat. It owns NO store of its own:
    //   the queue IS a named queue in NamedQueueService (the open "Queues" table the
    //   generic Queue.* node band writes), so !join is exactly
    //   queue.push(login, display, Config.QueueName, weight). That is deliberate and
    //   load-bearing — it is what makes every verb this part exposes reachable from an
    //   Architect graph, and what lets a graph watch the line through Queue.OnChanged
    //   whether the entry arrived by chat or by node. Eligibility reads the same
    //   EffectiveRoles answer above; there is no second role model.
    //
    // Shape mirrors SchedulingService (always-on, self-gated, master Config.Enabled
    // gates BEHAVIOUR). The one tick loop it does own is the queue's overlay
    // heartbeat, and only because a live-channel key MUST declare a publish cadence to
    // decay honestly — see QueueOverlayLoopAsync.
    public sealed class UserManagementService
    {
        private readonly DB _db;
        public UserManagementService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static UserManagementService? _instance;
        private static readonly object _instanceGate = new();
        public static UserManagementService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new UserManagementService(DB.Instance);
            }
        }

        // ── Seams (wired by ScriptManager.UserManagement.cs; null-safe) ──────
        /// <summary>Post one greeting to the platform the message arrived on.
        /// Args: (originalMessage, resolvedGreeting).</summary>
        public Func<ChatMessage, string, Task>? SendReply { get; set; }
        /// <summary>Fire a Twitch shoutout for the given login (Twitch-only).</summary>
        public Func<string, Task>? Shoutout { get; set; }
        /// <summary>Raises a User.OnFirstMessage script event (wired in
        /// RegisterUserManagementCommands, the only place that reaches the dispatcher).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        /// <summary>
        /// The named-queue runtime backing the viewer queue. Public get / internal set
        /// mirrors <c>CountersService.LiveStore</c>: production cannot swap it, while the
        /// test assembly gives each test its OWN instance instead of sharing the
        /// process-wide singleton — the DB.Instance flake class taught us to build that
        /// isolation in from the start.
        ///
        /// The setter MOVES the change subscription with it. Subscribing only from
        /// <see cref="InitializeAsync"/> would have been a trap for exactly one caller —
        /// a swapped-in instance never reaches that method — and the symptom is silent:
        /// the queue works, the panel updates on its own actions, and only the overlay
        /// and the graph-driven refresh quietly stop happening.
        /// </summary>
        public NamedQueueService Queues
        {
            get => _queues;
            internal set
            {
                var next = value ?? NamedQueueService.Instance;
                if (ReferenceEquals(_queues, next)) return;
                _queues.QueueChanged -= OnNamedQueueChanged;
                _queues = next;
                _queues.QueueChanged += OnNamedQueueChanged;
            }
        }
        private NamedQueueService _queues = NamedQueueService.Instance;

        /// <summary>
        /// The Overlay Live Channel the viewer queue publishes <c>queue.&lt;name&gt;.list</c>
        /// into. Same public-get / internal-set contract as <see cref="Queues"/>, and the
        /// same reason: per-test isolation without a production seam.
        /// </summary>
        public OverlayLiveStore LiveStore { get; internal set; } = OverlayLiveStore.Instance;

        // ── Config (swapped wholesale; volatile ⇒ visible on the chat path) ──
        private volatile UserManagementConfig _config = new();
        public UserManagementConfig Config => _config;
        /// <summary>Master gate — true only when the streamer enabled the tool.</summary>
        public bool Active => _config.Enabled;

        // ── Change events (crash-safe SafeEvent; UI-side MUST marshal) ───────
        public event EventHandler? ConfigChanged;
        public event EventHandler? RuntimeChanged;
        private void RaiseConfigChanged()
            => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "UserManagementService", "ConfigChanged");
        private void RaiseRuntimeChanged()
            => SafeEvent.Raise(RuntimeChanged, this, EventArgs.Empty, "UserManagementService", "RuntimeChanged");

        // ── Group index (rebuilt on config swap; read lock-free on hot paths) ─
        //
        // Only the groups that HAVE a member list live here. Moderator / VIP / Subscriber
        // are answered by the platform (the message's own flags, or the presence service's
        // role cache), so there is nothing about them to index.
        //
        // The watch-hour thresholds are carried IN the index rather than read from
        // _config beside it, so one volatile read gives a caller a consistent view: a
        // config swap between reading the members and reading the threshold would
        // otherwise let a role check answer from a state no single config ever had.
        private sealed class GroupIndex
        {
            public readonly HashSet<string> Regulars = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Watch HOURS after which a viewer counts as a Regular without
            /// being on the list. 0 = no rule.</summary>
            public int RegularWatchHours;
            /// <summary>(sanitized var key, members, watch-hour rule) per custom group.</summary>
            public readonly List<(string Key, HashSet<string> Members, int WatchHours)> Customs = new();
        }
        private volatile GroupIndex _index = new();

        // The four standard result-var keys are RESERVED — a custom group whose
        // name sanitizes onto one of them would clobber the standard group's
        // group.* output (last-write-wins in SetLocalResultVar). All four stay
        // reserved even though only "regular" still has a member list here: the
        // other three are still emitted as group.* outputs (from the platform's
        // answer), so a custom group landing on one of those keys would still be
        // a silent role-check inversion.
        private static readonly HashSet<string> ReservedGroupKeys = new(StringComparer.Ordinal)
        {
            "moderator", "vip", "subscriber", "regular",
        };

        private static GroupIndex BuildIndex(UserManagementConfig cfg)
        {
            var ix = new GroupIndex();
            ix.RegularWatchHours = cfg.RegularWatchHours;
            // The lists are null-checked, not just empty-checked: every one of them is a
            // property on a blob that arrives from JSON, and an explicit `"Regulars": null`
            // (a hand-edited config, or one written by an older build) deserializes to null
            // over the initializer. A tool that cannot rebuild its index cannot answer a
            // single role check.
            if (cfg.Regulars is not null)
                foreach (var n in cfg.Regulars) if (!string.IsNullOrWhiteSpace(n)) ix.Regulars.Add(n.Trim());
            if (cfg.CustomGroups is null) return ix;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in cfg.CustomGroups)
            {
                if (g is null) continue;
                string key = UserGroupKeys.Sanitize(g.Name);
                if (key.Length == 0) continue;
                // A custom group colliding with a reserved standard key, or with an
                // earlier custom group's key (e.g. "Night Crew" vs "night-crew"),
                // is IGNORED (first wins) — a silent role-check inversion is worse
                // than a dead duplicate. Logged once per rebuild, non-modal.
                if (ReservedGroupKeys.Contains(key) || !seenKeys.Add(key))
                {
                    GlobalLogger.Log(
                        $"User-Management: custom group '{g.Name}' ignored — its key '{key}' collides with a standard group or another custom group. Rename it.",
                        "UserManagementService", LogLevel.System);
                    continue;
                }
                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (g.Members is not null)
                    foreach (var n in g.Members) if (!string.IsNullOrWhiteSpace(n)) members.Add(n.Trim());
                ix.Customs.Add((key, members, g.AutoWatchHours));
            }
            return ix;
        }

        // Group members are keyed by LOGIN, but Twitch fills ChatMessage.Username
        // with the DISPLAY name (possibly localized/non-ASCII — the QC06-03
        // bot-guard lesson). Check the login first and fall back to the display
        // name so both spellings in the member list keep working.
        //
        // THE one identity resolver for the whole tool: the chat role answer, the
        // User.GetGroups node handler, the generic-event Regular check and the
        // watch-hour rule beside it all route through it, so a login-vs-display
        // mismatch can never be answered four different (and differently wrong) ways
        // again. Both candidates are trimmed because BuildIndex trims the members it
        // stores.
        //
        // ★ The display-name fallback is TWITCH-ONLY, and that is a security line, not
        // tidiness. Twitch binds the display name 1:1 to the account (it is the login's
        // own casing / localized form), so honouring it grants nothing a stranger can
        // claim. YouTube and Kick let any viewer pick any display name at any time, and
        // Regular is a tickable role on every gate in the suite — automod exemption,
        // !permit authority, the queue mod verbs, !delquote, counter set/reset — so
        // matching a free-form name there would hand those rights to whoever copies a
        // member's name. (The other three standard groups are out of reach of this
        // question entirely now: the platform answers them, not a list.)
        //
        // ★★ Known REMAINING exposure, recorded rather than papered over: on YouTube
        // both identity slots are the same free-form string, because the platform has no
        // separate handle and WS.TryBuildYouTubeChatMessage fills msg.Login from the very
        // `user.name` it puts in msg.Username. The login branch above is therefore still
        // display-name matching on YouTube, and no rule inside this file can tell the two
        // apart. Closing it needs a stable identity ON the message (the platform's numeric
        // user id — WS extracts one, ChatMessage does not carry it) plus platform-scoped
        // group entries; both live outside this file.
        private static bool MemberOf(HashSet<string> set, string? login, string? display, string? platform = null)
        {
            if (!string.IsNullOrWhiteSpace(login) && set.Contains(login.Trim())) return true;
            if (!DisplayNameIsAccountBound(platform)) return false;
            return !string.IsNullOrWhiteSpace(display) && set.Contains(display.Trim());
        }

        // A null/empty platform means the caller held no ChatMessage (the generic-event
        // and node lookups pass identities straight from a payload). ChatMessage.Platform
        // itself defaults to Twitch, so those callers keep the exact fallback behaviour
        // they have always had.
        private static bool DisplayNameIsAccountBound(string? platform)
            => string.IsNullOrEmpty(platform)
            || string.Equals(platform, ChatPlatforms.Twitch, StringComparison.OrdinalIgnoreCase);

        // ── The watch-hour rule ──────────────────────────────────────────────

        // Regular and every custom group can additionally be EARNED: a viewer with at
        // least `hours` accrued in the open "WatchTime" table counts as a member without
        // appearing on the list. Computed here on every check and never materialised into
        // Members, which is what makes the rule reversible — lower the threshold and the
        // whole channel qualifies at once, raise it and the grant is gone, with not one
        // row rewritten.
        //
        // Safe on the chat hot path: MeetsWatchHours is a lookup against
        // ViewerPresenceService's in-memory mirror of the table, never SQLite. It also
        // answers false until that mirror has loaded, so a Hub that has just started
        // cannot mass-demote everyone the rule had promoted.
        //
        // A non-positive threshold means NO RULE and is never met — a blank field must
        // not silently promote the entire channel.
        //
        // ★ Identity resolution is MemberOf's, verbatim: the login first, the display
        // name only where the platform binds it to the account. One rule for the list and
        // the rule means a viewer is the same person to both. On Twitch the fallback is
        // usually a second lookup of the same key (the display name IS the login's casing,
        // and both sides lowercase), and that is fine — what it must never become is a
        // free-form-name match on YouTube/Kick, which is exactly what
        // DisplayNameIsAccountBound is holding shut. Read the ★★ note above MemberOf
        // before widening either of them.
        private static bool MeetsWatchRule(int hours, string? login, string? display, string? platform = null)
        {
            if (hours <= 0) return false;
            var presence = ViewerPresenceService.Instance;
            if (!string.IsNullOrWhiteSpace(login) && presence.MeetsWatchHours(login.Trim(), hours)) return true;
            if (!DisplayNameIsAccountBound(platform)) return false;
            return !string.IsNullOrWhiteSpace(display) && presence.MeetsWatchHours(display.Trim(), hours);
        }

        // Regular membership, both ways in: the hand-picked list OR the watch-hour rule.
        // THE one place that composition lives, so the chat path, the generic-event path
        // and the node lookup can never answer it three differently-wrong ways.
        private static bool RegularFrom(GroupIndex ix, string? login, string? display, string? platform)
            => MemberOf(ix.Regulars, login, display, platform)
            || MeetsWatchRule(ix.RegularWatchHours, login, display, platform);

        // ── Effective roles (the suite-wide role answer) ─────────────────────

        /// <summary>The resolved role answer for one chat message: the platform's own
        /// Sub / VIP / Mod / Broadcaster flags, plus the Regular tier this tool owns.
        /// Nothing in Phoenix can promote a viewer past the platform any more — those
        /// three ranks are read here, never granted.</summary>
        public readonly struct EffectiveRoles
        {
            public EffectiveRoles(bool isSub, bool isVip, bool isMod, bool isBroadcaster, bool isRegular)
            { IsSub = isSub; IsVip = isVip; IsMod = isMod; IsBroadcaster = isBroadcaster; IsRegular = isRegular; }
            public bool IsSub { get; }
            public bool IsVip { get; }
            public bool IsMod { get; }
            public bool IsBroadcaster { get; }
            public bool IsRegular { get; }
        }

        /// <summary>
        /// The role answer every check in Phoenix consults: BuildChatVars, node outputs,
        /// and the built-in tools' role gates. Cheap (hash lookups only).
        ///
        /// <para>Sub / VIP / Mod / Broadcaster are the flags the PLATFORM stamped on this
        /// very message — authoritative, free, and available for every viewer who actually
        /// typed. This tool no longer adds anything to them: the three ranks it used to
        /// keep parallel member lists for are the platform's to define, and a local list
        /// that disagreed silently outranked it everywhere. Only <c>IsRegular</c> is this
        /// tool's own answer (manual list OR watch-hour rule), and only while the tool is
        /// enabled — a dormant tool reports the platform's flags unchanged and nobody as a
        /// Regular.</para>
        /// </summary>
        public EffectiveRoles Effective(ChatMessage msg)
        {
            if (msg is null) return new EffectiveRoles(false, false, false, false, false);
            if (!Active)
                return new EffectiveRoles(msg.IsSub, msg.IsVip, msg.IsMod, msg.IsBroadcaster, false);
            return new EffectiveRoles(
                msg.IsSub, msg.IsVip, msg.IsMod, msg.IsBroadcaster,
                RegularFrom(_index, msg.Login, msg.Username, msg.Platform));
        }

        /// <summary>Regular-group membership for generic-event vars, where no
        /// ChatMessage exists: pass whichever identities the payload carried —
        /// login first, display name as the fallback (either may be empty). Goes
        /// through the same resolver as the chat path, so a viewer whose display
        /// name differs from their login is no longer invisible to the group, and
        /// covers both ways in (hand-picked list, watch-hour rule). False while
        /// the tool is dormant.</summary>
        public bool IsRegular(string? login, string? display = null)
        {
            if (!Active) return false;
            return RegularFrom(_index, login, display, null);
        }

        /// <summary>
        /// Full group lookup for the callers that hold no ChatMessage — the User.GetGroups
        /// node handler, twitch.get_user, twitch.check_role: the four standard flags plus
        /// (key, member) per existing custom group. All-false / empty while the tool is
        /// dormant (the node stays honest, not stale). Takes the same login-first,
        /// display-name-fallback pair as the chat path; callers that hold only one
        /// spelling pass it as the login and leave the fallback empty.
        ///
        /// <para><b>Where Moderator / VIP / Subscriber come from here.</b> There is no
        /// message to read the platform's flags off, so they come from
        /// <see cref="ViewerPresenceService"/>'s role cache — populated by every inbound
        /// chat line, topped up with the lurkers by the presence sweep.</para>
        ///
        /// <para>★ <b>A false in those three slots is "the platform has not said yes",
        /// which folds two different facts together</b> — "observed, and not a moderator"
        /// and "never observed at all". This signature cannot carry the difference (its
        /// shape is destructured by three call sites), so the honest handling is: use
        /// <see cref="LookupGroupsAsync"/> wherever the caller can afford to await. That
        /// overload resolves a never-observed login through a real platform lookup instead
        /// of guessing, and only a genuine miss pays anything. Reach for this synchronous
        /// one when you cannot await — and read its answer as a floor, not a verdict.</para>
        /// </summary>
        public (bool Moderator, bool Vip, bool Subscriber, bool Regular,
                List<(string Key, bool Member)> Customs) LookupGroups(string? login, string? display = null)
        {
            var early = EarlyGroupAnswer(login, display, out var ix);
            if (early is not null) return early.Value;
            // The unknown-is-no overload, deliberately: this path cannot await the
            // resolution, and the ★ note above says so where a caller would read it.
            var roles = ViewerPresenceService.Instance.RolesFor(PresenceIdentity(login, display));
            return ComposeGroups(ix, roles, login, display);
        }

        /// <summary>
        /// <see cref="LookupGroups"/> for the callers that CAN await — today the
        /// <c>usermgmt.get_groups</c> handler behind the User.GetGroups node.
        ///
        /// <para>Identical answer and identical tuple, with one difference that matters:
        /// a login the role cache has never seen is resolved on demand through
        /// <see cref="ViewerPresenceService.ResolveRolesAsync"/> (the Hub's "Phoenix: Get
        /// User" round-trip) instead of being reported as a non-moderator. A cached login
        /// costs a hash lookup and no round-trip, misses are negatively cached for five
        /// minutes, and the whole thing is rate-limited inside that service — so a graph
        /// that asks about the same viewer on every line pays for at most one lookup.</para>
        /// </summary>
        public async Task<(bool Moderator, bool Vip, bool Subscriber, bool Regular,
                           List<(string Key, bool Member)> Customs)> LookupGroupsAsync(
            string? login, string? display = null)
        {
            var early = EarlyGroupAnswer(login, display, out var ix);
            if (early is not null) return early.Value;
            var roles = await ViewerPresenceService.Instance
                .ResolveRolesAsync(PresenceIdentity(login, display)).ConfigureAwait(false);
            return ComposeGroups(ix, roles, login, display);
        }

        // The two guards both lookups share: nothing to look up at all, and the dormant
        // tool. A non-null return IS the whole answer — and it also hands back the ONE
        // read of the volatile index the caller must then keep using, so a config swap
        // mid-lookup cannot make the custom keys and the memberships disagree.
        private (bool Moderator, bool Vip, bool Subscriber, bool Regular,
                 List<(string Key, bool Member)> Customs)? EarlyGroupAnswer(
            string? login, string? display, out GroupIndex ix)
        {
            ix = _index;
            if (string.IsNullOrWhiteSpace(login) && string.IsNullOrWhiteSpace(display))
                return (false, false, false, false, new List<(string Key, bool Member)>());
            if (!Active)
            {
                // Dormant: still enumerate the custom-group KEYS (so wired sockets
                // resolve to "false" instead of an unset var) but grant nothing.
                var customs = new List<(string Key, bool Member)>(ix.Customs.Count);
                foreach (var g in ix.Customs) customs.Add((g.Key, false));
                return (false, false, false, false, customs);
            }
            return null;
        }

        // The spelling the platform-role cache is keyed under: the login when the caller
        // had one, else whatever single name they did have.
        //
        // There is deliberately NO display-name fallback for the CACHE, unlike the member
        // lists: the cache is keyed by the platform's own login, and on Twitch a display
        // name lowercases to exactly that login — so a second lookup under the display
        // name would either hit the same key or, on the platforms where the two genuinely
        // differ, match a free-form name somebody else could simply type into their
        // profile. A fallback that can only be a no-op or an impersonation is not a
        // fallback worth having.
        private static string PresenceIdentity(string? login, string? display)
            => !string.IsNullOrWhiteSpace(login) ? login!.Trim() : (display ?? string.Empty).Trim();

        // Builds the answer once the platform roles are in hand, so the sync and async
        // lookups can differ ONLY in how they got them.
        private static (bool Moderator, bool Vip, bool Subscriber, bool Regular,
                        List<(string Key, bool Member)> Customs) ComposeGroups(
            GroupIndex ix, PlatformRoles roles, string? login, string? display)
        {
            var customs = new List<(string Key, bool Member)>(ix.Customs.Count);
            foreach (var g in ix.Customs)
                customs.Add((g.Key, MemberOf(g.Members, login, display)
                                 || MeetsWatchRule(g.WatchHours, login, display)));
            return (roles.IsMod, roles.IsVip, roles.IsSub,
                    RegularFrom(ix, login, display, null), customs);
        }

        // ── Welcoming ────────────────────────────────────────────────────────

        private readonly object _seenGate = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private int _greetedThisSession;

        // Lifetime known-chatters set (first-time greeting). Never cleared by the
        // stream lifecycle — only the panel's explicit reset empties it.
        private readonly object _seenEverGate = new();
        private readonly HashSet<string> _seenEver = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Users marked welcomed this stream (for the stat tile).</summary>
        public int SeenCount { get { lock (_seenGate) return _seen.Count; } }
        /// <summary>Greetings actually sent since Hub start (for the stat tile).</summary>
        public int GreetedThisSession => _greetedThisSession;
        /// <summary>Lifetime known chatters (for the greeting card's state line).</summary>
        public int KnownChatterCount { get { lock (_seenEverGate) return _seenEver.Count; } }

        /// <summary>
        /// The most recently first-seen chatters, newest first — the one DURABLE slice of
        /// this tool's history, because the first-seen timestamp has always been persisted
        /// alongside the name and only the read threw it away.
        ///
        /// <para>Distinct from the session ring on purpose: these rows survive a Hub restart
        /// and the ring's do not, so a surface that shows both must label them apart rather
        /// than merge them.</para>
        ///
        /// <para>The timestamp is NULL for a row written by a build that predates the
        /// column, which stores 0. Null means "known chatter, date unrecorded" — never
        /// 1970, which is what a bare conversion would print.</para>
        ///
        /// <para>Returns an empty list rather than throwing when the read fails — a history
        /// panel must never be the thing that takes the tool down.</para>
        /// </summary>
        public async Task<IReadOnlyList<(string Login, DateTimeOffset? FirstSeenUtc)>> RecentFirstSeenAsync(int limit = 50)
        {
            try
            {
                var rows = await _db.LoadUserMgmtSeenEverRecentAsync(limit).ConfigureAwait(false);
                var result = new List<(string, DateTimeOffset?)>(rows.Count);
                foreach (var (name, ms) in rows)
                    result.Add((name, ToFirstSeen(ms)));
                return result;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "recent first-seen read failed", ex);
                return Array.Empty<(string, DateTimeOffset?)>();
            }
        }

        // 0 is the pre-column default, and anything Unix milliseconds cannot represent is a
        // hand-edited row — both are "unrecorded", not a date.
        internal static DateTimeOffset? ToFirstSeen(long ms)
        {
            if (ms <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        // ── Test seams (in-memory tests must not touch the live databank or WS) ──
        /// <summary>Connectivity probe — swapped by tests; production reads WS.</summary>
        internal Func<bool> IsConnectedProbe = static () => WS.Instance.IsConnected;
        /// <summary>False in tests: skip the fire-and-forget DB mark persistence.</summary>
        internal bool PersistMarks = true;
        /// <summary>Test-only config swap that rebuilds the index WITHOUT persisting.</summary>
        internal void SetConfigForTest(UserManagementConfig cfg)
        {
            _config = cfg ?? new UserManagementConfig();
            _index = BuildIndex(_config);
        }

        // ── Live-state — deliberately defaults NOT-live, unlike the Scheduling/
        // Loyalty siblings. Their flag GATES behaviour (default-live keeps a
        // no-live-detection setup working); ours only drives the per-stream
        // RESET on the offline→live TRANSITION. Defaulting true would swallow
        // the first StreamOnline after every Hub boot (wasLive already true ⇒ no
        // clear), so the welcomed-set would NEVER reset on the normal
        // launch-Hub-then-go-live flow. With false, boot→go-live clears; and a
        // mid-stream Hub restart only re-greets if the platform re-emits
        // StreamOnline for the already-live stream (rare, and one benign
        // re-greet cycle beats a permanently stale set).
        private volatile bool _streamLive = false;
        /// <summary>Wired from ScriptManager StreamOnline/Offline (beside the Timer /
        /// Scheduling / Loyalty calls). The offline→live transition is the per-stream
        /// welcomed-set reset; a mid-stream Hub restart (no transition) keeps the
        /// DB-persisted set, so nobody is greeted twice.</summary>
        public void SetStreamLive(bool live)
        {
            bool wasLive = _streamLive;
            _streamLive = live;
            if (live && !wasLive)
            {
                lock (_seenGate) _seen.Clear();
                if (PersistMarks)
                {
                    _ = AsyncErrorBoundary.SafeRunAsync(
                        () => _db.ClearUserMgmtSeenAsync(),
                        "UserManagementService", "clear welcomed-set on going live");
                }
                RaiseRuntimeChanged();
            }
        }

        /// <summary>
        /// The observe-only chat tap (registered as a built-in chat provider, after
        /// Automod so moderated messages never greet). Marks the user seen on their
        /// FIRST message this stream (and known forever on their first message ever)
        /// and picks at most ONE line to send: the personalized row when the username
        /// has an enabled row (with optional Twitch shoutout), else the once-ever
        /// first-time greeting for brand-new chatters, else the general welcome.
        /// Always leaves the message for later providers/scripts.
        /// </summary>
        public async Task OnChatMessageAsync(ChatMessage msg)
        {
            var cfg = _config;
            if (!cfg.Enabled) return;
            bool welcomingOn = cfg.WelcomingEnabled;
            bool greetingOn = cfg.GreetingEnabled;
            // Marks still record when BOTH halves are off (groups-only setups) —
            // the panel promises the known-chatters baseline builds "as soon as
            // the tool is enabled", which is what makes turning the greeting on
            // later safe for regulars. Only the send branches need a half on.
            bool canSend = welcomingOn || greetingOn;
            if (msg is null || string.IsNullOrWhiteSpace(msg.Username)) return;
            // The broadcaster chatting in their own channel is not a "viewer arriving".
            if (msg.IsBroadcaster) return;
            // Streamer.bot down ⇒ every send silently drops. Don't burn the user's
            // one first-message moment on it — leave them unmarked so the greeting
            // fires on their next message once the connection is back. (Same
            // IsConnected pre-check SchedulingService's tick uses.) Baseline-only
            // mode records regardless: "has chatted before" is true either way.
            if (canSend && !IsConnectedProbe()) return;

            // Seen-set key: the stable platform LOGIN when the payload carried one
            // (Twitch Username is the DISPLAY name), else the lowercased username.
            string login = (!string.IsNullOrWhiteSpace(msg.Login) ? msg.Login : msg.Username)
                .Trim().ToLowerInvariant();

            // Lifetime mark FIRST — recorded whenever the tool observes chat (either
            // half on), so the known-chatters baseline builds even while the greeting
            // toggle is still off. Enabling it later then only greets genuine
            // newcomers instead of every regular's next message.
            bool firstEver;
            lock (_seenEverGate) firstEver = _seenEver.Add(login);
            if (firstEver)
            {
                // Bounded by DISTINCT chatters, not by messages: the set add above is what
                // makes this at-most-once per login for the life of the known-chatters set,
                // so this costs nothing on the per-message path it sits on.
                RecordActivity("SEEN", $"{ClipForActivity(login)} chatted here for the first time.");
            }
            if (firstEver && PersistMarks)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => _db.AddUserMgmtSeenEverAsync(login, nowMs),
                    "UserManagementService", "persist known-chatter mark");
            }

            bool firstThisSession;
            lock (_seenGate) firstThisSession = _seen.Add(login);
            if (firstThisSession && PersistMarks)
            {
                // Persist the mark fire-and-forget — a lost write only risks one duplicate
                // greeting after a crash, never a dropped chat message.
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => _db.AddUserMgmtSeenAsync(login),
                    "UserManagementService", "persist welcomed mark");
            }
            if (firstEver || firstThisSession)
            {
                // The stat tiles count marked users even when every message branch
                // below ends up disabled/empty.
                RaiseRuntimeChanged();
            }

            // User.OnFirstMessage — raised HERE, on the one decision the service already
            // makes and a graph cannot cheaply reproduce: "this is their first line this
            // stream". Deliberately BEFORE every send branch below, and gated only on the
            // master toggle, so it fires for a groups-only or baseline-only setup too —
            // the event is a fact about the viewer, not a side-effect of the greeting.
            // A viewer whose first-ever mark landed but whose session mark did not (an
            // upgrade from a build that predates the lifetime set) is not "arriving", so
            // the raise rides firstThisSession exactly like the send branches do.
            if (firstThisSession) RaiseFirstMessage(msg, login, firstEver);

            // Baseline-only mode (master on, both halves off): recording was the
            // whole job.
            if (!canSend) return;

            // At most one line per user per stream: everything below rides the
            // first-message-this-session moment. (A first-EVER user is by definition
            // also first-this-session; the one exception — already welcomed this
            // stream by a build that predates the lifetime set — stays silent and is
            // simply recorded above.)
            if (!firstThisSession) return;

            // Personalized row wins; disabled/empty rows fall back to the generic
            // branches. Rows are keyed by login but match the display name too, so
            // either spelling in the tool works. Rows are part of the WELCOMING half.
            WelcomeEntry? personal = null;
            if (welcomingOn)
            {
                foreach (var e in cfg.PersonalWelcomes)
                {
                    if (!e.Enabled) continue;
                    string rowName = e.Username?.Trim() ?? "";
                    if (rowName.Length == 0) continue;
                    if (!string.Equals(rowName, msg.Username.Trim(), StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(rowName, login, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(rowName, msg.Login?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    personal = e;
                    break;
                }
            }

            string message;
            bool shoutout = false;
            // A blank greeting template must fall through to the general welcome —
            // otherwise it would win the precedence chain, send nothing, and burn
            // the user's only greeting moment this stream (same blank-guard the
            // personal-row arm has always had).
            bool greetingUsable = firstEver && greetingOn && !string.IsNullOrWhiteSpace(cfg.GreetingMessage);
            if (personal is not null && !string.IsNullOrWhiteSpace(personal.Message))
            {
                message = personal.Message;
                shoutout = personal.AutoShoutout;
            }
            else if (personal is not null && personal.AutoShoutout)
            {
                // Personalized row with shoutout but no custom text: shout, then use
                // the best generic line so the row still does something useful.
                message = greetingUsable ? cfg.GreetingMessage
                    : cfg.GeneralWelcomeEnabled ? cfg.GeneralWelcomeMessage
                    : "";
                shoutout = true;
            }
            else if (greetingUsable)
            {
                // Brand-new chatter: the once-ever greeting beats the general welcome
                // on this one message (they'd otherwise get greeted twice in spirit —
                // per-stream welcoming naturally covers them from next stream on).
                message = cfg.GreetingMessage;
            }
            else
            {
                if (!welcomingOn || !cfg.GeneralWelcomeEnabled) return;
                message = cfg.GeneralWelcomeMessage;
            }

            string resolved = ResolveWelcomeTokens(message, msg);
            bool didSomething = false;

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                var send = SendReply;
                if (send is not null)
                {
                    try { await send(msg, resolved).ConfigureAwait(false); didSomething = true; }
                    catch (Exception ex) { GlobalLogger.Error("UserManagementService", "welcome send failed", ex); }
                }
            }

            // Shoutout is a Twitch feature — only meaningful when the first message
            // arrived on Twitch (SB's shoutout targets a Twitch channel).
            if (shoutout && string.Equals(msg.Platform, ChatPlatforms.Twitch, StringComparison.OrdinalIgnoreCase))
            {
                var so = Shoutout;
                if (so is not null)
                {
                    try { await so(login).ConfigureAwait(false); didSomething = true; }
                    catch (Exception ex) { GlobalLogger.Error("UserManagementService", "welcome shoutout failed", ex); }
                }
            }

            if (didSomething)
            {
                System.Threading.Interlocked.Increment(ref _greetedThisSession);
                RecordActivity("GREET", shoutout
                    ? $"Welcomed {ClipForActivity(msg.Username)} and fired a shoutout."
                    : $"Welcomed {ClipForActivity(msg.Username)}.");
                RaiseRuntimeChanged();
            }
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "UserManagement";

        // Logins and display names are viewer-supplied; group names are streamer-supplied.
        // Both are unbounded, so both go through here.
        private const int ActivityFieldMaxChars = 40;

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return "someone";
            return t.Length <= ActivityFieldMaxChars ? t : t[..ActivityFieldMaxChars].TrimEnd() + "...";
        }

        // Observation only: the SEEN row sits on the per-message chat tap (behind a
        // once-per-login gate) and the GREET row on the send path, so a fault in either
        // must never reach the caller.
        private static void RecordActivity(string kind, string message)
        {
            try { ToolActivityRing.Record(ActivityTool, kind, message); }
            catch (Exception ex) { GlobalLogger.Error("UserManagementService", "activity record failed", ex); }
        }

        // User.OnFirstMessage vars. event.login is bound even though the node exposes no
        // socket for it: the DISPLAY name is what a greeting prints, but a group check or
        // a databank row needs the stable login, and a graph reaching {event.login} by
        // hand should find it there. The token set is pinned in three more places (the
        // exporter arm, AutocompleteScopeBuilder and VarChainAnalyzer.ResultEmitterMap).
        private void RaiseFirstMessage(ChatMessage msg, string login, bool firstEver)
        {
            var raise = RaiseScriptEvent;
            if (raise == null) return;
            try
            {
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event.user"] = msg.Username ?? "",
                    ["event.login"] = login ?? "",
                    ["event.message"] = msg.Message ?? "",
                    ["event.platform"] = msg.Platform ?? "",
                    ["event.first_ever"] = firstEver ? "true" : "false",
                };
                raise("User.OnFirstMessage", vars);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "RaiseScriptEvent(User.OnFirstMessage) failed", ex);
            }
        }

        internal static string ResolveWelcomeTokens(string template, ChatMessage msg)
        {
            if (string.IsNullOrEmpty(template)) return "";
            // {platform} first, {user} LAST — the username is external input; a
            // display name containing a literal "{platform}" must not get a second
            // substitution pass.
            return template
                .Replace("{platform}", msg.Platform ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{user}", msg.Username ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ── Viewer queue ─────────────────────────────────────────────────────
        // The tool's fourth part. It stores nothing here: every mutation goes through
        // Queues (NamedQueueService) against Config.QueueName, i.e. the exact same
        // named queue an Architect graph addresses with Queue.Push / Pop / Position /
        // Remove / List / Clear. The only local state is a cached length for the stat
        // tile and the overlay heartbeat's transition latch.

        /// <summary>Gate for the queue's chat provider: the master toggle AND the
        /// section gate. Re-read per message by the dispatcher, so a live toggle takes
        /// effect on the next line.</summary>
        public bool QueueChatActive => _config.Enabled && _config.QueueEnabled;

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says, over THREE independent facts: the master
        /// toggle, whether any greeting can actually reach a viewer
        /// (<see cref="WelcomesReachAnyone"/>), and the queue's own section gate.
        ///
        /// ★ The welcoming dimension is why this is not a two-input machine. The master
        /// gates the WHOLE viewer queue as well as the welcoming
        /// (<see cref="QueueChatActive"/> is <c>Enabled &amp;&amp; QueueEnabled</c>), so
        /// "off" means the line is off too — but "on" says nothing at all about whether
        /// anyone gets welcomed: <see cref="OnChatMessageAsync"/> reaches its send
        /// branches only when <c>WelcomingEnabled</c> or <c>GreetingEnabled</c> is on,
        /// and both of those sit under their own switches. A groups-only setup (master
        /// on so the role overlay applies, both greeting halves off) is a real
        /// configuration, and the pill must not describe it as welcoming anyone.
        /// </summary>
        public enum UserManagementPillState
        {
            /// <summary>The tool is switched off — the viewer queue goes with it.</summary>
            DormantQueueOffToo,
            /// <summary>On, but nothing greets and the queue's section gate is off. What
            /// still runs is the group role overlay (and the silent known-chatter
            /// baseline behind it) — the groups-only setup.</summary>
            IdleGroupsOnly,
            /// <summary>On and a greeting really can go out, but the queue's own section
            /// gate is off, so <c>!join</c> does nothing. (Named for the welcoming half
            /// this state reports on; the pill's own word is the house "armed".)</summary>
            WelcomingQueueOff,
            /// <summary>Queue live, but no greeting can reach anyone — the line answers
            /// its chat verbs while every arrival passes unwelcomed.</summary>
            LiveQueueNoWelcome,
            /// <summary>On with the queue live and greetings reaching viewers.</summary>
            LiveWithQueue,
        }

        /// <summary>
        /// Pure state machine behind <see cref="PillState"/>.
        ///
        /// <paramref name="welcomesReachAnyone"/> defaults to <c>true</c> so the
        /// two-argument call sites that predate the welcoming dimension keep compiling
        /// and keep their old answer; every production read goes through
        /// <see cref="PillState"/>, which always passes the real value.
        /// </summary>
        internal static UserManagementPillState ComputePillState(
            bool enabled, bool queueEnabled, bool welcomesReachAnyone = true)
        {
            if (!enabled) return UserManagementPillState.DormantQueueOffToo;
            if (!welcomesReachAnyone)
                return queueEnabled
                    ? UserManagementPillState.LiveQueueNoWelcome
                    : UserManagementPillState.IdleGroupsOnly;
            if (!queueEnabled) return UserManagementPillState.WelcomingQueueOff;
            return UserManagementPillState.LiveWithQueue;
        }

        /// <summary>
        /// True when at least one branch of <see cref="OnChatMessageAsync"/> can put a
        /// line in chat for an arriving viewer. Mirrors that method's own gates rather
        /// than any single toggle:
        ///
        ///   * the once-ever greeting sends on <c>GreetingEnabled</c> alone, but a BLANK
        ///     template is deliberately not usable there (it falls through instead of
        ///     burning the viewer's one moment), so it only counts with text;
        ///   * the general welcome needs <c>WelcomingEnabled</c> AND
        ///     <c>GeneralWelcomeEnabled</c>, and a blank message resolves to nothing sent;
        ///   * an enabled personalized row greets under <c>WelcomingEnabled</c> even with
        ///     the general welcome off — as text, as a Twitch shoutout, or both.
        ///
        /// Not modelled: whether the platform a shoutout-only row fires on is Twitch (the
        /// shoutout arm checks that per message). That narrows to "a row that only shouts,
        /// in a non-Twitch channel", which no toggle on the page can express.
        /// </summary>
        internal static bool WelcomesReachAnyone(UserManagementConfig? cfg)
        {
            if (cfg is null) return false;
            if (cfg.GreetingEnabled && !string.IsNullOrWhiteSpace(cfg.GreetingMessage)) return true;
            if (!cfg.WelcomingEnabled) return false;
            if (cfg.GeneralWelcomeEnabled && !string.IsNullOrWhiteSpace(cfg.GeneralWelcomeMessage)) return true;
            var rows = cfg.PersonalWelcomes;
            if (rows is null) return false;
            foreach (var e in rows)
            {
                if (e is null || !e.Enabled) continue;
                if (string.IsNullOrWhiteSpace(e.Username)) continue;
                if (!string.IsNullOrWhiteSpace(e.Message) || e.AutoShoutout) return true;
            }
            return false;
        }

        /// <summary>The live pill state. <see cref="QueueCount"/> supplies the "N in line"
        /// half of the label.</summary>
        public UserManagementPillState PillState
        {
            get
            {
                // One read of the volatile field for all three inputs — a config swap
                // mid-computation must not produce a state no single config ever had.
                var cfg = _config;
                return ComputePillState(cfg.Enabled, cfg.QueueEnabled, WelcomesReachAnyone(cfg));
            }
        }

        /// <summary>The queue's name in the open "Queues" table. Never empty — an empty
        /// Name is what selects the LEGACY unnamed pipe-string queue in the Queue.* band,
        /// and silently putting viewers in THAT would make the whole line invisible to
        /// every named-queue node and to the panel.</summary>
        public string EffectiveQueueName
        {
            get
            {
                string n = (_config.QueueName ?? "").Trim();
                return n.Length == 0 ? "viewers" : n;
            }
        }

        private volatile int _queueCount;
        /// <summary>Entries currently waiting (for the stat tile). Cached rather than
        /// read live because the panel's stat projections are synchronous; refreshed on
        /// load and on every QueueChanged, including script-driven ones.</summary>
        public int QueueCount => _queueCount;

        // Per-user cooldown across the queue's chat verbs. Monotonic Stopwatch clock —
        // never wall clock — so an NTP step or a DST change can't unblock a cooldown
        // early. Keyed by normalized user. Mirrors the Counters/Loyalty pattern, but
        // lives HERE rather than in ScriptManager because the whole verb — parse, gate,
        // mutate, reply text — is service-side, which is what keeps it testable
        // in-memory the way the greeting half is.
        private readonly object _queueCdGate = new();
        private readonly Dictionary<string, long> _queueUserCdMs = new(StringComparer.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _queueMono =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Outcome of offering one chat line to the queue. <see cref="Handled"/>
        /// stops the built-in dispatch (the message was a queue command); <see cref="Reply"/>
        /// is what to post, and may be empty for a handled-but-silent case such as a
        /// role rejection — a repeatable rejection never gets a chat line, only a log.</summary>
        public readonly struct QueueChatResult
        {
            public QueueChatResult(bool handled, string reply) { Handled = handled; Reply = reply; }
            public bool Handled { get; }
            public string Reply { get; }
            public static readonly QueueChatResult NotHandled = new(false, "");
            public static QueueChatResult Silent() => new(true, "");
            public static QueueChatResult Say(string reply) => new(true, reply ?? "");
        }

        /// <summary>
        /// The queue's built-in chat commands. Viewer verbs: <c>!&lt;join&gt;</c>,
        /// <c>!&lt;leave&gt;</c>, <c>!&lt;position&gt;</c>, and <c>!&lt;queue&gt;</c> to see the
        /// line. Moderator sub-verbs ride the list command: <c>!&lt;queue&gt; &lt;next&gt;</c>
        /// takes the front entry, <c>!&lt;queue&gt; &lt;pick&gt; &lt;user&gt;</c> takes a specific
        /// one out of order, <c>!&lt;queue&gt; &lt;remove&gt; &lt;user&gt;</c> drops one,
        /// <c>!&lt;queue&gt; &lt;clear&gt;</c> empties it. All four sub-verb words are
        /// CONFIGURED (<c>QueueNextSubCommand</c> and its three siblings) and matched
        /// through <see cref="ChatVerb"/> like every other verb — they were string
        /// literals in this parser, which made them the only queue words a streamer could
        /// neither see in the panel nor change.
        ///
        /// Returns NotHandled for anything that is not a queue command, so later
        /// providers and authored on_chat scripts still see the message.
        /// </summary>
        public async Task<QueueChatResult> TryHandleQueueChatAsync(ChatMessage msg)
        {
            if (!QueueChatActive || msg is null) return QueueChatResult.NotHandled;

            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return QueueChatResult.NotHandled;

            var cfg = _config;
            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return QueueChatResult.NotHandled;
            string rest = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();

            // Verb match goes through the one shared canonicalizer. This tool has no
            // config-normalization pass at all, so a configured "!join" reached the
            // comparison verbatim against a token whose '!' the parse above had
            // already removed — the queue verbs were dead for anyone who typed the
            // bang into the field. ChatVerb keeps the empty-never-matches rule this
            // local Eq carried. Argument order is (token, configured); ChatVerb
            // canonicalizes both sides. The four management SUB-verbs go through the
            // same comparison, in MatchQueueSubVerb.
            static bool Eq(string a, string b) => ChatVerb.Matches(b, a);

            // `list` is passed on to RunQueueModVerbAsync as the verb it echoes back
            // in its replies, so it is canonicalized here rather than only compared —
            // otherwise a configured "!queue" would print "!queue next" back at the
            // streamer with the bang doubled.
            string list = ChatVerb.Canonical(cfg.QueueListCommand);

            bool isJoin = Eq(token, cfg.QueueJoinCommand ?? "");
            bool isLeave = Eq(token, cfg.QueueLeaveCommand ?? "");
            bool isList = Eq(token, list);
            bool isPosition = Eq(token, cfg.QueuePositionCommand ?? "");
            if (!isJoin && !isLeave && !isList && !isPosition) return QueueChatResult.NotHandled;

            // Roles resolve off the SAME EffectiveRoles answer every other built-in tool
            // gate consults — Mod/VIP/Sub are the platform's flags on this very message,
            // and the Regular tick resolves off the group store this very tool owns
            // (manual list or watch-hour rule).
            var eff = Effective(msg);
            bool modOk = (cfg.QueueModRoles ?? QueueRoles.Mods())
                .Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);

            // Moderator sub-verbs first: they ride the list command, and a mod typing
            // "!queue clear" must not be answered with the plain line.
            if (isList && rest.Length > 0)
            {
                int subSp = rest.IndexOf(' ');
                string sub = (subSp < 0 ? rest : rest.Substring(0, subSp)).Trim();
                string subArg = subSp < 0 ? string.Empty : rest.Substring(subSp + 1).Trim();
                var modVerb = MatchQueueSubVerb(cfg, sub, out string subWord);
                if (modVerb != QueueSubVerb.None)
                {
                    if (!modOk) return QueueChatResult.Silent();
                    return await RunQueueModVerbAsync(cfg, list, modVerb, subWord, subArg).ConfigureAwait(false);
                }
                // An unrecognised tail is not a management verb — fall through and print
                // the line, which is what "!queue anything" most plausibly meant.
            }

            bool joinOk = (cfg.QueueJoinRoles ?? QueueRoles.All())
                .Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);
            if (!joinOk) return QueueChatResult.Silent();

            string login = QueueLoginOf(msg);
            string display = (msg.Username ?? "").Trim();
            if (login.Length == 0) return QueueChatResult.Silent();

            // The cooldown is claimed only once we know a viewer verb will actually run,
            // so a role rejection above never burns the caller's budget. Bucketed by
            // operation class exactly like the Counters cooldown: joining and leaving are
            // the expensive verbs, while "where am I" and "show the line" are reads and
            // must not be blocked by the budget a join just spent.
            bool isModifyVerb = isJoin || isLeave;
            if (!QueueCooldownOk(login, cfg.QueueCooldownSeconds, isModifyVerb))
                return QueueChatResult.Silent();

            string queue = EffectiveQueueName;
            if (isJoin)
            {
                int existing = await Queues.PositionAsync(queue, login).ConfigureAwait(false);
                if (existing > 0)
                {
                    int total = await RefreshQueueCountAsync(queue).ConfigureAwait(false);
                    return QueueChatResult.Say(QueueReply(cfg.QueueAlreadyMessage, display, existing, total, ""));
                }
                // The cap is a SOFT one: the store has no capped push, so two joins
                // arriving in the same instant could both read count == max-1. Chat is
                // dispatched one message at a time, so it takes a genuinely simultaneous
                // multi-platform arrival to hit — and one extra viewer in a line the
                // streamer can trim from the panel is a far better outcome than a
                // transaction shape invented for it.
                int count = await Queues.LengthAsync(queue).ConfigureAwait(false);
                if (cfg.QueueMaxSize > 0 && count >= cfg.QueueMaxSize)
                {
                    SetQueueCount(count);
                    return QueueChatResult.Say(QueueReply(cfg.QueueFullMessage, display, 0, count, ""));
                }
                long weight = (eff.IsSub ? cfg.QueueSubPriority : 0) + (eff.IsVip ? cfg.QueueVipPriority : 0);
                int len = await Queues.PushAsync(queue, login, display, weight).ConfigureAwait(false);
                SetQueueCount(len);
                int place = await Queues.PositionAsync(queue, login).ConfigureAwait(false);
                return QueueChatResult.Say(QueueReply(cfg.QueueJoinedMessage, display, place, len, ""));
            }

            if (isLeave)
            {
                var gone = await Queues.RemoveAsync(queue, login).ConfigureAwait(false);
                int total = await RefreshQueueCountAsync(queue).ConfigureAwait(false);
                return QueueChatResult.Say(gone is null
                    ? QueueReply(cfg.QueueNotQueuedMessage, display, 0, total, "")
                    : QueueReply(cfg.QueueLeftMessage, display, 0, total, ""));
            }

            if (isPosition)
            {
                int place = await Queues.PositionAsync(queue, login).ConfigureAwait(false);
                int total = await RefreshQueueCountAsync(queue).ConfigureAwait(false);
                return QueueChatResult.Say(place > 0
                    ? QueueReply(cfg.QueuePositionMessage, display, place, total, "")
                    : QueueReply(cfg.QueueNotQueuedMessage, display, 0, total, ""));
            }

            // Plain list.
            var rows = await Queues.ListAsync(queue).ConfigureAwait(false);
            SetQueueCount(rows.Count);
            if (rows.Count == 0) return QueueChatResult.Say(QueueReply(cfg.QueueEmptyMessage, display, 0, 0, ""));
            return QueueChatResult.Say(QueueReply(cfg.QueueListMessage, display, 0, rows.Count, QueueNamesCsv(rows)));
        }

        /// <summary>The four management sub-verbs, once resolved from config. An enum
        /// rather than the typed word, so nothing downstream can re-derive the meaning of
        /// a streamer-supplied string a second (and differently wrong) time.</summary>
        private enum QueueSubVerb { None, Next, Pick, Remove, Clear }

        // Which configured sub-verb the typed word is, plus the CONFIGURED spelling to
        // echo back (canonical, bang stripped) — a reply that names a sub-verb must name
        // the word the streamer set, not a literal this file used to hard-code.
        //
        // A BLANK field is a DISABLED sub-verb, not a fall-back to the old literal:
        // ChatVerb.Matches never matches an empty configured verb (that rule is what stops
        // a blank field from swallowing every bang in chat), and the panel's command list
        // drops an empty verb for the same reason — so both surfaces agree that the word
        // is gone. The tail then falls through to the plain list print, exactly like any
        // other unrecognised tail.
        //
        // Two fields holding the same word resolve to the first in declaration order;
        // nothing else could tell them apart.
        private static QueueSubVerb MatchQueueSubVerb(UserManagementConfig cfg, string token, out string word)
        {
            if (ChatVerb.Matches(cfg.QueueNextSubCommand, token))
            { word = ChatVerb.Canonical(cfg.QueueNextSubCommand); return QueueSubVerb.Next; }
            if (ChatVerb.Matches(cfg.QueuePickSubCommand, token))
            { word = ChatVerb.Canonical(cfg.QueuePickSubCommand); return QueueSubVerb.Pick; }
            if (ChatVerb.Matches(cfg.QueueRemoveSubCommand, token))
            { word = ChatVerb.Canonical(cfg.QueueRemoveSubCommand); return QueueSubVerb.Remove; }
            if (ChatVerb.Matches(cfg.QueueClearSubCommand, token))
            { word = ChatVerb.Canonical(cfg.QueueClearSubCommand); return QueueSubVerb.Clear; }
            word = string.Empty;
            return QueueSubVerb.None;
        }

        // `verbWord` is the streamer's own spelling of the sub-verb that matched, carried
        // in only so the usage line can print it. It is deliberately NOT lower-cased on
        // the way out: matching is case-insensitive, so echoing "Next" back at a streamer
        // who configured "Next" is both correct and what they will recognise.
        private async Task<QueueChatResult> RunQueueModVerbAsync(
            UserManagementConfig cfg, string listCommand, QueueSubVerb verb, string verbWord, string arg)
        {
            string queue = EffectiveQueueName;

            if (verb == QueueSubVerb.Clear)
            {
                await Queues.ClearAsync(queue).ConfigureAwait(false);
                SetQueueCount(0);
                return QueueChatResult.Say(QueueReply(cfg.QueueClearedMessage, "", 0, 0, ""));
            }

            if (verb == QueueSubVerb.Next)
            {
                var head = await Queues.PopAsync(queue).ConfigureAwait(false);
                int total = await RefreshQueueCountAsync(queue).ConfigureAwait(false);
                if (head is null) return QueueChatResult.Say(QueueReply(cfg.QueueEmptyMessage, "", 0, 0, ""));
                // Payload carries the display name the viewer joined with, so the
                // announcement uses their own spelling rather than the lowercased login.
                return QueueChatResult.Say(QueueReply(cfg.QueueNextMessage, QueueDisplayOf(head), 0, total, ""));
            }

            // pick / remove both address one named entry; the only difference is which
            // line they announce — "you're up" versus "you're out".
            string target = QueueNormalize(arg);
            // A missing name is an operator typo, and a mod who gets silence back cannot
            // tell that from "it worked". Answer with usage — the same call
            // TryHandleCountersChatCommandAsync makes for a malformed !set<cmd>. Both
            // words in it are the streamer's own configured ones, so the line is a
            // copy-pasteable command rather than a description of one.
            if (target.Length == 0)
                return QueueChatResult.Say($"Usage: !{listCommand} {verbWord} <user>");
            var hit = await Queues.RemoveAsync(queue, target).ConfigureAwait(false);
            int left = await RefreshQueueCountAsync(queue).ConfigureAwait(false);
            if (hit is null) return QueueChatResult.Say(QueueReply(cfg.QueueNotQueuedMessage, arg.Trim(), 0, left, ""));
            bool pick = verb == QueueSubVerb.Pick;
            return QueueChatResult.Say(QueueReply(
                pick ? cfg.QueueNextMessage : cfg.QueueRemovedMessage,
                QueueDisplayOf(hit), 0, left, ""));
        }

        // Identity key for the queue: the stable platform LOGIN when the payload carried
        // one (Twitch fills ChatMessage.Username with the DISPLAY name), else the
        // lowercased username — the exact rule the welcomed-set uses, so a viewer is one
        // person to both halves of the tool.
        private static string QueueLoginOf(ChatMessage msg)
            => QueueNormalize(!string.IsNullOrWhiteSpace(msg.Login) ? msg.Login : msg.Username);

        private static string QueueNormalize(string? s)
            => (s ?? "").Trim().TrimStart('@').ToLowerInvariant();

        // The display name an entry joined with, falling back to its login when a graph
        // pushed the entry with no payload — a script-driven queue.push is a first-class
        // way into this line, so it must still print something.
        private static string QueueDisplayOf(QueueEntry e)
            => string.IsNullOrWhiteSpace(e.Payload) ? e.Entry : e.Payload;

        // The chat list is CAPPED — the only one of the three surfaces that is not free to
        // print the whole line (the panel scrolls, the overlay board clamps to 1..100).
        //
        // SendTwitchChatCore DROPS a message over Twitch's 500-character cap outright: one
        // CriticalError in the System Log and no reply at all. Nothing upstream bounds the
        // input either — QueueMaxSize defaults to 0, i.e. unlimited — so an uncapped join
        // makes !<queue> print NOTHING exactly when the line is busiest and someone
        // actually wants to read it. The overlay board's clamp already carries the
        // argument ("a board nobody can read is not worth an unbounded payload"); these
        // two limits are that same argument applied to chat.
        //
        // BOTH a name count and a character budget are needed. The count is what keeps the
        // line readable; the budget is what keeps it deliverable, because a display name
        // arrives from chat — or, for a script-driven queue.push, from a payload of the
        // author's choosing — and carries no length guarantee of its own. The budget is
        // charged against the names alone, leaving the rest of the 500 to the streamer's
        // own QueueListMessage template around {list}.
        private const int QueueChatListMaxNames = 15;
        private const int QueueChatListMaxChars = 300;

        // Comma-joined names for {list}, cut at whichever limit binds first and tailed
        // with how many did not fit, so a truncated line reads as truncated instead of as
        // the whole queue. The first name is always printed, however long it is: a reply
        // that names nobody is no better than the dropped message this exists to prevent.
        //
        // The tail is fixed English like the mod verbs' "Usage:" line rather than another
        // config template — it describes the transport's limit, not anything the streamer
        // authored, and a template with no {list} in it never reaches here at all.
        private static string QueueNamesCsv(List<QueueEntry> rows)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var r in rows)
            {
                if (shown >= QueueChatListMaxNames) break;
                string name = QueueDisplayOf(r);
                if (shown > 0 && sb.Length + 2 + name.Length > QueueChatListMaxChars) break;
                if (shown > 0) sb.Append(", ");
                sb.Append(name);
                shown++;
            }
            int hidden = rows.Count - shown;
            if (hidden > 0)
                sb.Append(" …and ").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more");
            return sb.ToString();
        }

        /// <summary>Reply-template substitution. Tokens: {user}, {position}, {count},
        /// {list}. {count} is the line's TRUE length; {list} is the capped, tailed name
        /// list from <see cref="QueueNamesCsv"/>, so the two disagree on a long queue by
        /// design. {user} is substituted LAST because it is external input — a display
        /// name containing a literal "{count}" must not get a second pass.</summary>
        internal static string QueueReply(string template, string user, int position, int count, string list)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return template
                .Replace("{position}", position.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{count}", count.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{list}", list ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{user}", user ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // Per-user cooldown check + stamp. True = clear to run (and stamped). Bucketed by
        // operation class (READ vs MODIFY) so a benign "where am I" never consumes the
        // budget a join needs — the same split ScriptManager.Counters.cs applies.
        private bool QueueCooldownOk(string user, int cooldownSeconds, bool isModify)
        {
            if (cooldownSeconds <= 0) return true;
            string key = (isModify ? "m\0" : "r\0") + user;
            long now = _queueMono.ElapsedMilliseconds;
            lock (_queueCdGate)
            {
                if (_queueUserCdMs.TryGetValue(key, out long end) && now < end) return false;
                _queueUserCdMs[key] = now + cooldownSeconds * 1000L;
                return true;
            }
        }

        private void SetQueueCount(int n)
        {
            if (_queueCount == n) return;
            _queueCount = n;
            RaiseRuntimeChanged();
        }

        private async Task<int> RefreshQueueCountAsync(string queue)
        {
            int n = await Queues.LengthAsync(queue).ConfigureAwait(false);
            SetQueueCount(n);
            return n;
        }

        /// <summary>The current line, front first — the panel's list and the overlay
        /// payload both read through here.</summary>
        public Task<List<QueueEntry>> ListQueueAsync() => Queues.ListAsync(EffectiveQueueName);

        /// <summary>Panel action: drop one entry by its login. Mirrors the chat
        /// <c>!&lt;queue&gt; remove</c> verb exactly — same store, same event, same overlay
        /// refresh — so the two surfaces can't diverge.</summary>
        public async Task RemoveFromQueueAsync(string login)
        {
            await Queues.RemoveAsync(EffectiveQueueName, QueueNormalize(login)).ConfigureAwait(false);
            await RefreshQueueCountAsync(EffectiveQueueName).ConfigureAwait(false);
        }

        /// <summary>Panel action: empty the line (confirmed in the UI).</summary>
        public async Task ClearQueueAsync()
        {
            await Queues.ClearAsync(EffectiveQueueName).ConfigureAwait(false);
            SetQueueCount(0);
        }

        // Any mutation from ANYWHERE — chat verb, panel button, or a script's
        // queue.push — lands here, so the stat tile and the overlay never fall behind a
        // graph-driven change. Fire-and-forget through AsyncErrorBoundary because the
        // event is raised synchronously from inside NamedQueueService.
        private void OnNamedQueueChanged(object? sender, string queue)
        {
            if (!string.Equals(queue, EffectiveQueueName, StringComparison.OrdinalIgnoreCase)) return;
            _ = AsyncErrorBoundary.SafeRunAsync(async () =>
            {
                await RefreshQueueCountAsync(EffectiveQueueName).ConfigureAwait(false);
                await PublishQueueOverlayAsync().ConfigureAwait(false);
            }, "UserManagementService", "queue change refresh");
        }

        // ── Queue overlay (Live Channel) ─────────────────────────────────────

        private const string QueueLiveSource = "tool:UserManagement";

        /// <summary>
        /// The queue's publish cadence, and the ExpectedInterval every queue key declares.
        ///
        /// ★ A live-channel key MUST declare a cadence to be honest, because the store has
        /// no remove API and no TTL: a key published once reports Active for the rest of
        /// the session, so a queue that stopped being maintained — tool switched off, Hub
        /// wedged, section gate flipped — would keep painting a line that no longer exists
        /// as live. Declaring the interval is only half of it, though; something has to
        /// keep publishing, or the key would report Stale within seconds of a quiet queue
        /// that is perfectly current. Hence the heartbeat: it republishes unconditionally,
        /// and the store COALESCES an identical value (refreshes LastWriteUtc, does not
        /// dirty the key, ships no frame), so an unchanged queue costs one dictionary
        /// write per tick and nothing on the wire.
        /// </summary>
        private static readonly TimeSpan QueueOverlayInterval = TimeSpan.FromSeconds(5);

        private readonly object _queueLoopGate = new();
        private CancellationTokenSource? _queueLoopCts;
        private Task? _queueLoopTask;
        private bool _queueLoopStarted;
        // Tracks whether the last tick published. The off-transition publishes ONE empty
        // board: List.Live keeps painting its last rows while Stale (by design — a board
        // that stopped updating should not blank mid-stream), so decaying alone would
        // leave a finished line on screen. An explicit empty array is the honest "gone".
        //
        // It is scoped to the key that was last published, which is why a RENAME has to
        // retract before the latch moves on — see RetractRenamedQueueKeyAsync.
        private bool _queueOverlayWasOn;
        // The heartbeat tick and the change hook both publish, so they are serialized:
        // otherwise two overlapping reads could land out of order and leave the channel
        // holding the OLDER line until the next tick corrected it. One publisher at a
        // time also makes _queueOverlayWasOn a plain field rather than a race.
        private readonly SemaphoreSlim _queuePublishGate = new(1, 1);

        private void StartQueueOverlayPump()
        {
            lock (_queueLoopGate)
            {
                if (_queueLoopStarted) return;
                _queueLoopStarted = true;
                _queueLoopCts = new CancellationTokenSource();
                var ct = _queueLoopCts.Token;
                _queueLoopTask = Task.Run(() => QueueOverlayLoopAsync(ct));
            }
        }

        private async Task QueueOverlayLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(QueueOverlayInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await PublishQueueOverlayAsync().ConfigureAwait(false); }
                catch (Exception ex) { GlobalLogger.Error("UserManagementService", "queue overlay publish failed", ex); }
            }
        }

        /// <summary>
        /// Publishes the line as <c>queue.&lt;name&gt;.list</c> (a JSON array of
        /// <c>{ position, name, login, priority }</c>) plus <c>queue.&lt;name&gt;.count</c>.
        ///
        /// The array shape is what List.Live reads: its default Format is
        /// <c>"{index}. {name}"</c> and its default Field is <c>name</c>, so a widget that
        /// binds the key and changes nothing else already prints a numbered line.
        /// </summary>
        private async Task PublishQueueOverlayAsync()
        {
            await _queuePublishGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cfg = _config;
                bool on = cfg.Enabled && cfg.QueueEnabled && cfg.QueueOverlayEnabled;
                if (!on)
                {
                    if (!_queueOverlayWasOn) return;
                    _queueOverlayWasOn = false;
                    string offKey = QueueLiveKey(EffectiveQueueName);
                    LiveStore.Publish($"queue.{offKey}.list", new JsonArray(), QueueLiveSource, QueueOverlayInterval);
                    LiveStore.PublishNumber($"queue.{offKey}.count", 0, QueueLiveSource, QueueOverlayInterval);
                    return;
                }

                string queue = EffectiveQueueName;
                var rows = await Queues.ListAsync(queue).ConfigureAwait(false);
                SetQueueCount(rows.Count);

                int size = cfg.QueueOverlaySize;
                if (size < 1) size = 1;
                if (size > 100) size = 100;

                var board = new JsonArray();
                for (int i = 0; i < rows.Count && i < size; i++)
                {
                    var r = rows[i];
                    board.Add(new JsonObject
                    {
                        ["position"] = r.Position,
                        ["name"] = QueueDisplayOf(r),
                        ["login"] = r.Entry,
                        ["priority"] = r.Priority,
                    });
                }

                string key = QueueLiveKey(queue);
                LiveStore.Publish($"queue.{key}.list", board, QueueLiveSource, QueueOverlayInterval);
                // The full length, NOT the truncated board size — "12 waiting" is the number a
                // viewer cares about even when the widget only shows the top 10.
                LiveStore.PublishNumber($"queue.{key}.count", rows.Count, QueueLiveSource, QueueOverlayInterval);
                _queueOverlayWasOn = true;
            }
            finally { _queuePublishGate.Release(); }
        }

        /// <summary>
        /// The CONFIG half of the queue's live-channel hygiene — the same job
        /// <c>CountersService.RetractDroppedCounterKeysAsync</c> does for a deleted
        /// counter, applied to the one edit that abandons a queue key: a RENAME.
        ///
        /// <see cref="PublishQueueOverlayAsync"/>'s off-transition cannot cover it. That
        /// edge fires only on enable→disable and only against the CURRENT name, so after a
        /// rename <c>queue.&lt;old&gt;.list</c> is never written again and never retracted:
        /// it keeps its last real rows, the store has no remove API and no TTL, and
        /// List.Live deliberately goes on painting a Stale board — so the old line stays
        /// on stream for the rest of the session, beside the new one.
        ///
        /// The retraction is an explicit EMPTY array plus a zero count, matching the
        /// off-transition rather than the counters' JSON-null tombstone, because these keys
        /// are array-shaped and an empty array is what List.Live reads as "nothing left".
        ///
        /// Cost on an ordinary debounced keystroke save is one string compare: the name did
        /// not change, so nothing else runs.
        /// </summary>
        private async Task RetractRenamedQueueKeyAsync(string previousKey)
        {
            if (string.Equals(previousKey, QueueLiveKey(EffectiveQueueName), StringComparison.Ordinal)) return;

            bool retracted = false;
            await _queuePublishGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Nothing was ever published under the old name (overlay off all along, or
                // already retracted by the off-transition), so there is nothing to take
                // back and no reason to mint a key for a queue that never painted.
                if (_queueOverlayWasOn)
                {
                    LiveStore.Publish($"queue.{previousKey}.list", new JsonArray(), QueueLiveSource, QueueOverlayInterval);
                    LiveStore.PublishNumber($"queue.{previousKey}.count", 0, QueueLiveSource, QueueOverlayInterval);
                    // The latch described the OLD key. Clearing it hands the arming back to
                    // the new name's first publish, and stops a later disable from
                    // retracting a name that never painted anything.
                    _queueOverlayWasOn = false;
                    retracted = true;
                }
            }
            finally { _queuePublishGate.Release(); }

            // Republish under the new name immediately instead of waiting out the
            // heartbeat: a rename that blanks the board for up to five seconds reads as a
            // broken widget. Outside the gate, because this takes it itself.
            if (retracted) await PublishQueueOverlayAsync().ConfigureAwait(false);
        }

        // Queue name → the key segment. Lower-casing is the ONLY normalisation, and
        // deliberately so: the browser derives its key with String(name).toLowerCase()
        // and never trims, so trimming here would publish queue.viewers.list while the
        // widget subscribed "queue. viewers.list" — a permanently blank readout with no
        // error anywhere. Same rule, and same reasoning, as CountersService.KeyName.
        private static string QueueLiveKey(string name) => (name ?? "").ToLowerInvariant();

        // ── Lifecycle ────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public async Task InitializeAsync()
        {
            // Seen-sets FIRST: the config swap below is what arms the chat provider
            // (ChatObservationActive), so both sets must already be in memory when
            // the first message can possibly flow — otherwise a restart mid-stream
            // would re-greet everyone who chats inside the load window.
            try
            {
                var seen = await _db.LoadUserMgmtSeenAsync().ConfigureAwait(false);
                lock (_seenGate)
                {
                    _seen.Clear();
                    foreach (var n in seen) _seen.Add(n);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "welcomed-set load failed", ex);
            }
            try
            {
                var ever = await _db.LoadUserMgmtSeenEverAsync().ConfigureAwait(false);
                lock (_seenEverGate)
                {
                    _seenEver.Clear();
                    foreach (var n in ever) _seenEver.Add(n);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "known-chatters load failed", ex);
            }

            try
            {
                string? json = await _db.LoadUserMgmtConfigAsync().ConfigureAwait(false);
                _config = string.IsNullOrWhiteSpace(json)
                    ? new UserManagementConfig()
                    : (JsonSerializer.Deserialize<UserManagementConfig>(json!, JsonOpts) ?? new UserManagementConfig());

                // The Moderator / VIP / Subscriber member lists were removed from the model
                // when those three groups became platform-defined. System.Text.Json drops
                // unknown properties without a word, so a streamer who had typed names into
                // them would watch the lists disappear on upgrade with no explanation and no
                // way to tell whether it was intentional. Say it once, with the counts, and
                // name what replaced it.
                ReportRetiredGroupListsOnce(json);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "config load failed", ex);
                _config = new UserManagementConfig();
            }
            _index = BuildIndex(_config);

            // Queue wiring LAST, after the config swap, because both halves read it:
            // the subscription filters on EffectiveQueueName and the first publish needs
            // to know whether the overlay is even on. Subscribing here (not in the ctor)
            // also keeps a test-constructed service inert until it is initialized.
            Queues.QueueChanged -= OnNamedQueueChanged;
            Queues.QueueChanged += OnNamedQueueChanged;
            try
            {
                _queueCount = await Queues.LengthAsync(EffectiveQueueName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "queue length load failed", ex);
            }
            // The pump runs unconditionally and self-gates inside, mirroring
            // SchedulingService's tick: the gate can flip at any time, and a loop that
            // had to be started and stopped by config edits is one more thing to keep in
            // step for no gain. A dormant tick costs one volatile read.
            StartQueueOverlayPump();

            RaiseConfigChanged();
        }

        /// <summary>Cancels the queue's overlay heartbeat and drains it. Config is
        /// already persisted on every edit (UpdateConfigAsync) and the queue itself lives
        /// in the databank, so there is nothing to flush — this only stops the pump from
        /// re-dirtying an Overlay Live Channel the shutdown coordinator already drained.
        /// Mirrors SchedulingService.ShutdownAsync.</summary>
        public async Task ShutdownAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_queueLoopGate)
            {
                cts = _queueLoopCts;
                loop = _queueLoopTask;
                _queueLoopStarted = false;
                _queueLoopCts = null;
                _queueLoopTask = null;
            }
            Queues.QueueChanged -= OnNamedQueueChanged;
            try { cts?.Cancel(); } catch { }
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { GlobalLogger.Error("UserManagementService", "queue overlay loop drain failed", ex); }
            }
            cts?.Dispose();
        }

        /// <summary>Forgets every known chatter — the panel's explicit destructive
        /// reset (confirmed in the UI). After this, the next message from ANYONE is
        /// "first ever" again. The per-stream welcomed-set clears TOO: every send
        /// branch rides the first-message-this-session moment, so a mid-stream reset
        /// that left _seen intact would silently consume each active chatter's
        /// re-minted first-ever mark with nothing sent — the exact opposite of what
        /// the confirm dialog promises. Never invoked by the stream lifecycle.</summary>
        public async Task ResetKnownChattersAsync()
        {
            lock (_seenEverGate) _seenEver.Clear();
            lock (_seenGate) _seen.Clear();
            if (PersistMarks)
            {
                try
                {
                    await _db.ClearUserMgmtSeenEverAsync().ConfigureAwait(false);
                    await _db.ClearUserMgmtSeenAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("UserManagementService", "known-chatters reset failed", ex);
                }
            }
            RaiseRuntimeChanged();
        }

        // ── Config writes ────────────────────────────────────────────────────
        // Both writers below take this gate, so a whole-config panel save and an
        // incremental group grant can never interleave their read-modify-persist steps and
        // leave the blob describing neither. It is NOT nested inside any other gate here;
        // UpdateConfigAsync's queue retraction takes _queuePublishGate afterwards, and
        // nothing takes them in the opposite order.
        private readonly SemaphoreSlim _configWriteGate = new(1, 1);

        /// <summary>Swaps in a new config (deep-owned by the caller), rebuilds the
        /// group index, persists, retracts the live-channel key of a queue the edit
        /// renamed away from, and notifies. The UI's single write path.</summary>
        public async Task UpdateConfigAsync(UserManagementConfig newConfig)
        {
            if (newConfig is null) return;
            string previousKey;
            await _configWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Captured BEFORE the swap, exactly like CountersService.SaveConfigAsync:
                // the config is replaced wholesale (the panel edits a clone), so the name
                // the overlay has been publishing under is only reachable from here.
                previousKey = QueueLiveKey(EffectiveQueueName);
                _config = newConfig;
                _index = BuildIndex(newConfig);
                try
                {
                    string json = JsonSerializer.Serialize(newConfig, JsonOpts);
                    await _db.SaveUserMgmtConfigAsync(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("UserManagementService", "config save failed", ex);
                }
            }
            finally { _configWriteGate.Release(); }

            await RetractRenamedQueueKeyAsync(previousKey).ConfigureAwait(false);
            RaiseConfigChanged();
        }

        // ── Incremental group grant ──────────────────────────────────────────

        /// <summary>Every group that EXISTS — the four standard ones followed by the
        /// streamer's custom groups, in config order. This is the CHECKING list: all four
        /// standard groups remain pickable wherever a role gate or a lookup names a group,
        /// because all four are still real memberships a viewer can hold.
        ///
        /// <para>It is NOT the list to offer where a group will be GRANTED — see
        /// <see cref="GrantableGroupNames"/>. Moderator / VIP / Subscriber are the
        /// platform's to hand out, and <see cref="GrantGroupAsync"/> refuses them.</para>
        /// </summary>
        public List<string> GroupNames()
        {
            var names = new List<string> { "Moderator", "VIP", "Subscriber", "Regular" };
            AppendCustomGroupNames(names);
            return names;
        }

        /// <summary>
        /// The groups another tool may actually grant INTO: Regular plus the streamer's
        /// custom groups. Drives the Ranks panel's rank-grant picker.
        ///
        /// <para>Deliberately a second method rather than a narrowing of
        /// <see cref="GroupNames"/>: the two questions genuinely differ now — "which groups
        /// exist" still answers four, "which can Phoenix put someone in" answers one plus
        /// the custom ones — and silently changing what the older name means would have
        /// dropped Moderator / VIP / Subscriber out of every picker that legitimately
        /// CHECKS them, with nothing at the call site to notice.</para>
        /// </summary>
        public List<string> GrantableGroupNames()
        {
            var names = new List<string> { "Regular" };
            AppendCustomGroupNames(names);
            return names;
        }

        private void AppendCustomGroupNames(List<string> names)
        {
            var groups = _config.CustomGroups;
            if (groups is null) return;
            foreach (var g in groups)
                if (!string.IsNullOrWhiteSpace(g?.Name)) names.Add(g!.Name.Trim());
        }

        /// <summary>
        /// Adds one login to one group — the NARROW incremental write that lets another
        /// tool (today: the Ranks ladder's optional rank grant) hand out role rights
        /// without owning this tool's whole config blob.
        ///
        /// ★ WHY THIS EXISTS RATHER THAN A CALLER-SIDE read-modify-<see cref="UpdateConfigAsync"/>.
        /// <c>EffectiveRoles</c> is computed on every read and has no write path of its
        /// own, so before this the only way in was to swap the entire blob — which races the
        /// User-Management panel's debounced whole-config rebuild, last-write-wins, and
        /// loses the grant with no error anywhere. This still cannot make a whole-blob UI
        /// writer safe on its own (no lock can: the panel builds its config from rows it
        /// read before taking any gate). What makes the grant sound is the pair of
        /// properties below, which the caller is expected to rely on:
        ///
        ///   * IDEMPOTENT — an already-granted login is answered <c>false</c> with NO write,
        ///     no persist and no ConfigChanged, so re-asserting costs one hash lookup.
        ///   * CONVERGENT — because it is idempotent, the caller may simply re-assert the
        ///     grant on every evaluation, and a grant clobbered by a panel save inside the
        ///     400 ms debounce is reapplied at the next one. RanksService does exactly that.
        ///
        /// A NEW instance is installed (rather than the live list being mutated in place)
        /// on purpose: the panel's self-trigger guard is reference-equality against the
        /// instance IT pushed, so an in-place mutation would leave the panel believing it
        /// was still looking at current data and its next save would drop the grant for
        /// good. Publishing a different instance makes the panel treat this as a foreign
        /// change and reload — which is what puts the grant in front of the streamer.
        ///
        /// Returns TRUE only when a row was actually added. A group name that resolves to
        /// nothing is a logged no-op, never an auto-created group: minting another tool's
        /// config behind the streamer's back is exactly the line every pre-build node
        /// holds. <c>Broadcaster</c> is not a group and is never grantable, and neither
        /// are <c>Moderator</c> / <c>VIP</c> / <c>Subscriber</c> — those three are the
        /// platform's answer and there is no list here to add a row to.
        /// </summary>
        public async Task<bool> GrantGroupAsync(string groupName, string login)
        {
            string who = (login ?? "").Trim().TrimStart('@');
            if (who.Length == 0) return false;
            string key = UserGroupKeys.Sanitize(groupName);
            if (key.Length == 0) return false;

            // Refused BEFORE the gate and before any clone: this is not "the group does
            // not exist" (it does, and it is checkable everywhere), it is "membership in
            // it is not ours to hand out", and the two need different sentences or the
            // streamer goes looking for a group they can plainly see in the picker.
            if (IsPlatformGroupKey(key))
            {
                WarnPlatformGrantOnce(groupName, key, who);
                return false;
            }

            await _configWriteGate.WaitAsync().ConfigureAwait(false);
            UserManagementConfig next;
            try
            {
                var live = _config;
                // Membership is checked against the INDEX's MEMBER LISTS, using the same
                // login-vs-display resolver every role gate reads.
                //
                // Deliberately NOT against the full gate answer: a viewer the watch-hour
                // rule already covers still gets written onto the list here. The rule is
                // reversible by design — raise the threshold and it stops granting — while
                // a rank's grant is meant to stick, so skipping the write because the rule
                // happens to be true today would quietly make the grant expire with it.
                var ix = _index;
                if (IsAlreadyMember(ix, key, who)) return false;

                next = Clone(live);
                if (!AddToGroup(next, key, who))
                {
                    GlobalLogger.Log(
                        $"User-Management: cannot grant \"{groupName}\" to {who} — no group with that name exists. " +
                        "Create it under Pre-Builds ▸ User Management, or clear the grant on the rank that asked for it.",
                        "UserManagementService", LogLevel.System);
                    return false;
                }

                _config = next;
                _index = BuildIndex(next);
                try
                {
                    string json = JsonSerializer.Serialize(next, JsonOpts);
                    await _db.SaveUserMgmtConfigAsync(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("UserManagementService", "group grant save failed", ex);
                }
            }
            finally { _configWriteGate.Release(); }

            GlobalLogger.Log($"User-Management: {who} added to the \"{groupName}\" group.",
                "UserManagementService", LogLevel.System);
            RecordActivity("GROUP", $"{ClipForActivity(who)} added to the \"{ClipForActivity(groupName)}\" group.");
            RaiseConfigChanged();
            return true;
        }

        /// <summary>The three standard groups whose membership the PLATFORM owns. They are
        /// still real groups — pickable, checkable, emitted as group.* outputs — they just
        /// have no member list here for anything to write into.</summary>
        private static bool IsPlatformGroupKey(string key)
            => key is "moderator" or "vip" or "subscriber";

        // One note per group key per process. RanksService RE-ASSERTS its grant on EVERY
        // evaluation (see its class doc: idempotent + convergent is what makes the grant
        // survive a whole-blob panel save), so an unthrottled refusal would print a line
        // per active viewer per watch-time tick — burying the one sentence the streamer
        // actually needs to read under thousands of copies of itself.
        private readonly HashSet<string> _platformGrantWarned = new(StringComparer.Ordinal);

        private void WarnPlatformGrantOnce(string groupName, string key, string who)
        {
            lock (_platformGrantWarned)
                if (!_platformGrantWarned.Add(key)) return;
            GlobalLogger.Log(
                $"User-Management: cannot grant \"{groupName}\" to {who} — Moderator, VIP and Subscriber " +
                "membership is whatever Twitch / YouTube / Kick says it is, and Phoenix can't hand it out. " +
                "Point that grant at Regular or a custom group instead (Pre-Builds ▸ User Management), or " +
                "clear it on the rank that asked for it. (One-time note per group.)",
                "UserManagementService", LogLevel.System);
        }

        /// <summary>
        /// Logs ONCE, at load, when the persisted blob still carries the retired
        /// Moderator / VIP / Subscriber member lists — naming how many entries each held so
        /// the streamer can see what was dropped rather than discovering it by a role gate
        /// quietly behaving differently.
        ///
        /// <para>Read straight off the raw JSON: the typed model no longer has the
        /// properties, and that is the whole reason the loss is invisible. Best-effort and
        /// entirely non-fatal — a blob this cannot parse is one the deserializer above
        /// already fell back on.</para>
        ///
        /// <para>Deliberately not a migration: there is nowhere to migrate the names TO.
        /// Those three groups are the platform's answer now, so a hand-typed list has no
        /// meaning to preserve. The honest action is to say what happened, once.</para>
        /// </summary>
        private static void ReportRetiredGroupListsOnce(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;
            if (Interlocked.Exchange(ref _retiredGroupsReported, 1) != 0) return;

            try
            {
                var node = JsonNode.Parse(rawJson!);
                if (node is null) return;

                var counts = new List<string>();
                foreach (var name in new[] { "Moderators", "Vips", "Subscribers" })
                {
                    if (node[name] is JsonArray arr && arr.Count > 0)
                        counts.Add($"{name}: {arr.Count}");
                }
                if (counts.Count == 0) return;

                GlobalLogger.Log(
                    "User-Management: the Moderator / VIP / Subscriber member lists were retired and their saved " +
                    $"entries are no longer used ({string.Join(", ", counts)}). Those three groups now come straight " +
                    "from Twitch / YouTube / Kick, so Phoenix no longer keeps its own copy — anyone the platform " +
                    "lists still passes those gates. To grant rights the platform does not, use the Regular group " +
                    "or a custom group, which can also be earned automatically through watch hours.",
                    "UserManagementService", LogLevel.System);
            }
            catch
            {
                // A blob too malformed to probe is one the deserializer already replaced
                // with defaults; there is nothing further worth saying about it.
            }
        }
        private static int _retiredGroupsReported;

        private static bool IsAlreadyMember(GroupIndex ix, string key, string who)
        {
            // The three platform groups never reach here — GrantGroupAsync refuses them
            // before the gate — which is why the standard-group arm is one branch and no
            // longer a four-case switch.
            if (string.Equals(key, "regular", StringComparison.Ordinal))
                return MemberOf(ix.Regulars, who, null);
            foreach (var g in ix.Customs)
                if (string.Equals(g.Key, key, StringComparison.Ordinal))
                    return MemberOf(g.Members, who, null);
            // An unknown group has no members, so "already a member" is false — the caller
            // then finds out it does not exist from AddToGroup, which is the one place that
            // can distinguish "not a member" from "not a group".
            return false;
        }

        // Writes the MANUAL list only — the watch-hour rule has no storage to write to,
        // which is the whole point of it. Also the one place that can tell "not a member"
        // from "not a group": false here means no group carried that key.
        private static bool AddToGroup(UserManagementConfig cfg, string key, string who)
        {
            if (string.Equals(key, "regular", StringComparison.Ordinal))
            {
                cfg.Regulars ??= new List<string>();
                cfg.Regulars.Add(who);
                return true;
            }
            if (cfg.CustomGroups is null) return false;
            foreach (var g in cfg.CustomGroups)
            {
                if (g is null || !string.Equals(UserGroupKeys.Sanitize(g.Name), key, StringComparison.Ordinal)) continue;
                g.Members ??= new List<string>();
                g.Members.Add(who);
                return true;
            }
            return false;
        }

        // Deep clone through a JSON round-trip, matching the panel VMs' own Clone: the new
        // instance must share no list with the live config, or the swap above would be a
        // swap in name only.
        private static UserManagementConfig Clone(UserManagementConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new UserManagementConfig(), JsonOpts);
                return JsonSerializer.Deserialize<UserManagementConfig>(json, JsonOpts) ?? new UserManagementConfig();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UserManagementService", "config clone failed", ex);
                return new UserManagementConfig();
            }
        }
    }
}
