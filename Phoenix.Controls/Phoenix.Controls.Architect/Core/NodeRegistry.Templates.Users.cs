using System.Collections.Generic;
using System.Drawing;

namespace Phoenix.Controls.Architect.Core
{
    // Users band — the User-Management group lookup surface ("Pre-Builds ▾ →
    // User Management", sibling to Loyalty / Timer). Architect-only authoring
    // surface: the Hub's UserManagementService owns the group store; this node
    // just reads it at runtime via usermgmt.get_groups.
    //
    // One node, Twitch.GetUser-shaped (flow lookup → bool result vars):
    //   User.GetGroups — Username in, one Bool out per group. The four STANDARD
    //   groups (Moderator / VIP / Subscriber / Regular) are fixed template
    //   outputs; the user-defined CUSTOM groups surface as ADDITIONAL Bool
    //   outputs synthesized from the node's "Groups" attribute (comma-separated
    //   group names — the DB.FetchRow KnownColumns pattern). The Architect UI
    //   refreshes that attribute from the Hub's group store on spawn/load, and
    //   it stays hand-editable as the fallback.
    //
    // Exporter contract: SimpleEmitDescriptor emits
    //   usermgmt.get_groups({Username})
    // and the "Users" arm in ScriptExporter.ResolveOutputFromNode maps each
    // output socket to the bare result-var key the Hub handler writes —
    // group.moderator / group.vip / group.subscriber / group.regular for the
    // standard four, and UserGroupKeys.VarKeyFor(socketName) ("group.<sanitized>")
    // for every custom-group socket. The handler enumerates the EXISTING custom
    // groups, so a socket for a since-deleted group resolves to the empty var
    // (falsy) instead of a stale value.
    public static partial class NodeRegistry
    {
        private static void RegisterUsersTemplates()
        {
            // Slate-blue header — reads as "identity/roles"; distinct from the
            // Twitch Data purple and the Loyalty money-green.
            Color users = Color.SteelBlue;

            AddTemplate("User.GetGroups", "Users", users,
                "Looks up which groups a user belongs to. Moderator / VIP / Subscriber are the PLATFORM's answer — Twitch, YouTube and Kick publish those ranks, so nobody keeps a list for them. Regular and your custom groups are the ones Phoenix owns: the names listed in the tool plus anyone past that group's watch-hour rule. Leave Username empty to check the triggering chatter. All outputs read false while the User-Management tool is disabled.",
                new[] { ("Flow", ColExec), ("Username", ColString) },
                new[] { ("Flow", ColExec),
                        ("IsModerator",  ColBool),
                        ("IsVip",        ColBool),
                        ("IsSubscriber", ColBool),
                        ("IsRegular",    ColBool) },
                new Dictionary<string, string>
                {
                    { "Username", "{user.name}" },
                    // Comma-separated custom-group names → one Bool output each
                    // (synthesized by EnsureUserGroupSockets; auto-refreshed from
                    // the Hub's group store by the canvas, hand-editable fallback).
                    { "Groups",   "" }
                });

            SetSocketDescriptions("User.GetGroups", new()
            {
                { "Username",     "Whose groups to check. Leave empty to check the triggering chatter ({user.name})." },
                { "IsModerator",  "True when the platform reports the user as a moderator. False also covers \"the platform has never told us about this account\" — not the same fact as \"not a moderator\"." },
                { "IsVip",        "True when the platform reports the user as a VIP (same not-known caveat as IsModerator)." },
                { "IsSubscriber", "True when the platform reports the user as a subscriber (same not-known caveat as IsModerator)." },
                { "IsRegular",    "True when the user is in the Regular group — the community-trust tier with no platform equivalent. Listed members, plus anyone past the group's watch-hour rule." },
            });

            // ── Event root (category "Events" — output-only) ─────────────────
            // MIRRORS Counter.OnChanged: null inputs, Flow output first. Category
            // "Events" makes it a script entry point and — importantly — keeps it OUT
            // of the blanket `Category == "Users"` arm in
            // ScriptExporter.ResolveOutputFromNode, which rewrites any unrecognised
            // output socket of a "Users" node into a {group.<socket>} bool. The
            // exporter emits on_event(User.OnFirstMessage) via ProcessEventNode's
            // trigger-switch fallback, matching the phoenixEvent string
            // UserManagementService raises, and a dedicated User.OnFirstMessage arm
            // maps User / Message away from the generic {user.*} overrides.
            //
            // WHY IT IS WORTH A NODE. The service already computes "is this their
            // first message this session / ever" to decide whether to greet, and that
            // decision is invisible to a graph: reproducing it in Architect means a
            // seen-table, a per-stream reset and a race on the first line of chat. The
            // event hands the same answer over and leaves what to DO with it — a
            // shoutout, a sound, a databank row, points — entirely to the author.
            AddTemplate("User.OnFirstMessage", "Events", users,
                "Fires the first time a viewer chats in a stream, at the moment the User Management tool notices them — before any welcome is sent. FirstEver is true when it is also their very first message in the channel ever. Needs the User Management tool switched on; it fires whether or not the welcome and greeting messages themselves are enabled.",
                null,
                new[] { ("Flow", ColExec),
                        ("User",      ColString),
                        ("Message",   ColString),
                        ("Platform",  ColString),
                        ("FirstEver", ColBool) });

            SetSocketDescriptions("User.OnFirstMessage", new()
            {
                { "User",      "The viewer's display name — the spelling to greet them by." },
                { "Message",   "The message they arrived with." },
                { "Platform",  "Where they chatted: twitch, youtube or kick." },
                { "FirstEver", "True when this is their first message in the channel EVER, not just this stream." },
            });

            SetKeywords("User.GetGroups",      "user", "users", "group", "groups", "role", "roles", "member", "regular");
            SetKeywords("User.OnFirstMessage", "user", "users", "first", "message", "arrive", "welcome", "greet", "new", "chatter", "event");
        }

        /// <summary>
        /// Ensures a <c>User.GetGroups</c> node carries one Bool output socket per
        /// entry in its <c>Groups</c> attribute (custom groups), in addition to the
        /// fixed Flow / standard-group sockets — the DB.FetchRow KnownColumns
        /// pattern. Existing custom sockets are preserved (socket Id stays stable so
        /// wires survive); sockets for groups no longer listed are removed along
        /// with any links wired to them. Idempotent — safe on every load and on
        /// every attribute edit.
        /// </summary>
        public static void EnsureUserGroupSockets(Phoenix.Controls.Shared.Models.Node node, Phoenix.Controls.Shared.Models.Graph? graph = null)
        {
            if (node?.Title != "User.GetGroups") return;

            var fixedNames = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "Flow", "Username", "IsModerator", "IsVip", "IsSubscriber", "IsRegular"
            };

            // SELF-SUFFICIENT shape heal: MigrateNodes takes the Event.*-style
            // `continue` for this node (the generic template prune would strip the
            // synthesized custom sockets — the same reason Event.* variable sockets
            // bypass it), so the fixed template shape must be guaranteed HERE.
            void EnsureFixed(string name, Phoenix.Controls.Shared.Models.SocketType type,
                             System.Drawing.Color color, Phoenix.Controls.Shared.Models.SocketDataType dataType)
            {
                foreach (var s in node.Sockets)
                    if (s.Type == type && s.Name == name) return;
                node.Sockets.Add(new Phoenix.Controls.Shared.Models.Socket
                {
                    Name = name, Type = type, Color = color, DataType = dataType,
                });
            }
            EnsureFixed("Flow",         Phoenix.Controls.Shared.Models.SocketType.Input,  ColExec,   Phoenix.Controls.Shared.Models.SocketDataType.Flow);
            EnsureFixed("Username",     Phoenix.Controls.Shared.Models.SocketType.Input,  ColString, Phoenix.Controls.Shared.Models.SocketDataType.String);
            EnsureFixed("Flow",         Phoenix.Controls.Shared.Models.SocketType.Output, ColExec,   Phoenix.Controls.Shared.Models.SocketDataType.Flow);
            EnsureFixed("IsModerator",  Phoenix.Controls.Shared.Models.SocketType.Output, ColBool,   Phoenix.Controls.Shared.Models.SocketDataType.Bool);
            EnsureFixed("IsVip",        Phoenix.Controls.Shared.Models.SocketType.Output, ColBool,   Phoenix.Controls.Shared.Models.SocketDataType.Bool);
            EnsureFixed("IsSubscriber", Phoenix.Controls.Shared.Models.SocketType.Output, ColBool,   Phoenix.Controls.Shared.Models.SocketDataType.Bool);
            EnsureFixed("IsRegular",    Phoenix.Controls.Shared.Models.SocketType.Output, ColBool,   Phoenix.Controls.Shared.Models.SocketDataType.Bool);
            node.Attributes.TryAdd("Username", "{user.name}");
            node.Attributes.TryAdd("Groups", "");

            // Parse the Groups attribute. Empty → no synthesized custom sockets.
            // Dedupe by SANITIZED KEY (not display name — "Night Crew" and
            // "night-crew" share one var key, so a second socket would read the
            // wrong group), and skip names whose key collides with a standard
            // group (the runtime index ignores those groups too — see
            // UserManagementService.ReservedGroupKeys).
            string raw = node.Attributes.TryGetValue("Groups", out var g) ? g ?? "" : "";
            var wanted = new List<string>();
            var wantedKeys = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "moderator", "vip", "subscriber", "regular",
            };
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var rawName in raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                {
                    string name = rawName.Trim('"');
                    if (string.IsNullOrEmpty(name)) continue;
                    if (fixedNames.Contains(name)) continue; // would collide with a built-in
                    string key = Phoenix.Controls.Shared.Models.UserGroupKeys.Sanitize(name);
                    if (key.Length == 0) continue;           // no usable var key
                    if (!wantedKeys.Add(key)) continue;      // reserved or duplicate key
                    wanted.Add(name);
                }
            }

            // Drop custom sockets whose group is no longer listed (collect ids so
            // the caller's graph sweep can prune their links).
            var dropIds = new List<string>();
            for (int i = node.Sockets.Count - 1; i >= 0; i--)
            {
                var s = node.Sockets[i];
                if (s.Type != Phoenix.Controls.Shared.Models.SocketType.Output) continue;
                if (fixedNames.Contains(s.Name)) continue;
                if (wanted.Contains(s.Name)) continue;
                dropIds.Add(s.Id);
                node.Sockets.RemoveAt(i);
            }

            // Append missing custom-group sockets in the declared order.
            var existingOutputNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var s in node.Sockets)
            {
                if (s.Type == Phoenix.Controls.Shared.Models.SocketType.Output)
                    existingOutputNames.Add(s.Name);
            }
            foreach (var name in wanted)
            {
                if (existingOutputNames.Contains(name)) continue;
                node.Sockets.Add(new Phoenix.Controls.Shared.Models.Socket
                {
                    Name     = name,
                    Type     = Phoenix.Controls.Shared.Models.SocketType.Output,
                    Color    = ColBool,
                    DataType = Phoenix.Controls.Shared.Models.SocketDataType.Bool,
                });
            }

            if (graph != null && dropIds.Count > 0)
            {
                graph.Links.RemoveAll(l => dropIds.Contains(l.FromSocketId) || dropIds.Contains(l.ToSocketId));
                graph.MarkStructuralChange();
            }

            // Re-lay-out the whole socket list + node height (spawn AND load share
            // this path — this node bypasses the generic template migration, so the
            // layout must be owned here). Constants mirror CreateNode's generic
            // factory (headerH 24 / spacing 22 / width 200).
            const int headerH = 24, spacing = 22;
            int nodeWidth = node.Size.Width > 0 ? node.Size.Width : 200;
            int inRow = 0, outRow = 0;
            foreach (var s in node.Sockets)
            {
                if (s.Type == Phoenix.Controls.Shared.Models.SocketType.Input)
                {
                    s.Offset = new System.Drawing.Point(-6, headerH + 6 + inRow * spacing);
                    inRow++;
                }
                else
                {
                    s.Offset = new System.Drawing.Point(nodeWidth - 14, headerH + 6 + outRow * spacing);
                    outRow++;
                }
            }
            int rows = System.Math.Max(inRow, outRow);
            node.Size = new System.Drawing.Size(nodeWidth, headerH + 14 + rows * spacing);
        }
    }

    /// <summary>
    /// Best-effort cache of the Hub group store's CUSTOM group names as the CSV
    /// the User.GetGroups "Groups" attribute consumes. Post-T15 the Architect
    /// pillar shares the Hub process, so DB.Instance is the live databank; every
    /// read is fully guarded — on any failure the cache simply stays stale/null
    /// and the node falls back to its persisted attribute (hand-editable).
    /// </summary>
    public static class UserGroupCatalog
    {
        private static volatile string? _cachedCsv;
        private static int _refreshing;

        /// <summary>Latest known custom-group CSV, or null before the first
        /// successful refresh (— keep the node's persisted attribute then).</summary>
        public static string? CachedCsv => _cachedCsv;

        /// <summary>
        /// Union-merges the cached catalog INTO a node's persisted Groups CSV:
        /// existing entries keep their order, cached groups not yet listed are
        /// appended. The auto-fill deliberately never REMOVES an entry — a stale /
        /// empty cache (fresh install, foreign graph, DB hiccup) must never strip
        /// sockets and cut their wires; removal stays a user edit of the attribute.
        /// </summary>
        public static string MergeCsv(string? existingCsv, string? cachedCsv)
        {
            var ordered = new List<string>();
            var seenKeys = new HashSet<string>(System.StringComparer.Ordinal);
            void AddAll(string? csv)
            {
                if (string.IsNullOrWhiteSpace(csv)) return;
                foreach (var raw in csv.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                {
                    string name = raw.Trim('"');
                    string key = Phoenix.Controls.Shared.Models.UserGroupKeys.Sanitize(name);
                    if (key.Length == 0) continue;
                    if (seenKeys.Add(key)) ordered.Add(name);
                }
            }
            AddAll(existingCsv);
            AddAll(cachedCsv);
            return string.Join(", ", ordered);
        }

        /// <summary>Kicks an async refresh (deduped). Callers never wait on it —
        /// the value lands for the NEXT spawn/load.</summary>
        public static void BeginRefresh()
        {
            // Only read a databank some other consumer ALREADY brought up (in the
            // Hub process that's the live DB). Never construct the singleton from
            // here — a design-time cache refresh (or a unit test spawning a node)
            // must not create/initialize the on-disk databank as a side effect.
            if (!Phoenix.Controls.Shared.Services.DB.HasInstance) return;
            if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string? json = await Phoenix.Controls.Shared.Services.DB.Instance
                        .LoadUserMgmtConfigAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) { _cachedCsv = ""; return; }
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<Phoenix.Controls.Shared.Models.UserManagementConfig>(
                        json!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cfg?.CustomGroups is null) { _cachedCsv = ""; return; }
                    var names = new List<string>();
                    foreach (var g in cfg.CustomGroups)
                    {
                        if (string.IsNullOrWhiteSpace(g?.Name)) continue;
                        if (Phoenix.Controls.Shared.Models.UserGroupKeys.Sanitize(g!.Name).Length == 0) continue;
                        // The attribute is a comma-separated list — a comma inside a
                        // group name would split into bogus fragments on the next
                        // parse, so flatten it to a space (the sanitized var key is
                        // identical either way).
                        names.Add(g.Name.Trim().Replace(',', ' '));
                    }
                    _cachedCsv = string.Join(", ", names);
                }
                catch { /* best effort — persisted attribute remains authoritative */ }
                finally { System.Threading.Volatile.Write(ref _refreshing, 0); }
            });
        }
    }
}
