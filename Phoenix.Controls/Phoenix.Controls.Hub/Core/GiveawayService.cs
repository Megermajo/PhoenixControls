using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Giveaway logic — the Hub-runtime brain of the giveaway system. Owns the
    // rules (slug generation, subscriber draw bonus, per-user cap, the weighted
    // winner draw) on top of the pure persistence in DB.Giveaway.cs. Both
    // front-ends drive THIS one service: the giveaway.* script commands
    // (ScriptManager.Giveaway.cs) and the Hub Giveaway page (via the
    // HubServices IGiveawaySource bridge). "One implementation, two front-ends."
    //
    // Pillar rule: this lives in the Hub runtime — the only process that touches
    // the DB and executes logic. Architect/Visualist never reference it.
    public sealed class GiveawayService
    {
        private readonly DB _db;

        public GiveawayService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        /// <summary>
        /// Pre-draw subscriber lookup, wired by ScriptManager at startup (Hub is
        /// the only Streamer.bot bridge; the service itself never talks to the
        /// wire). Input: entrant usernames. Output: username → is-subscriber for
        /// every entrant the lookup could resolve (case-insensitive keys), or
        /// null when Streamer.bot is unreachable. Unset (design/test hosts) or
        /// failing lookups fall back to the roles recorded at entry time.
        /// </summary>
        public Func<IReadOnlyList<string>, Task<IReadOnlyDictionary<string, bool>?>>? SubscriberStatusResolver { get; set; }

        // Shared instance over the singleton DB. Both front-ends (the giveaway.*
        // script commands and the Hub Giveaway page's HubServices bridge) resolve
        // THIS instance so they observe the same change events. Mirrors the
        // DB.Instance double-checked-locking pattern.
        private static GiveawayService? _instance;
        private static readonly object _instanceGate = new();
        public static GiveawayService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new GiveawayService(DB.Instance);
            }
        }

        /// <summary>Raised when the giveaway list or any giveaway's state changes.</summary>
        public event EventHandler? GiveawaysChanged;

        /// <summary>Raised with the affected giveaway id when its entrant pool changes.</summary>
        public event EventHandler<long>? EntrantsChanged;

        private void RaiseGiveaways() => SafeEvent.Raise(GiveawaysChanged, this, EventArgs.Empty, "GiveawayService", "GiveawaysChanged");
        private void RaiseEntrants(long id) => SafeEvent.Raise(EntrantsChanged, this, id, "GiveawayService", "EntrantsChanged");

        private static string NowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // ── Reads (used by the Hub page) ────────────────────────────────────
        public Task<List<Giveaway>> ListAsync() => _db.GetGiveawaysAsync();
        public Task<Giveaway?> GetAsync(long id) => _db.GetGiveawayAsync(id);
        public Task<List<GiveawayEntrant>> GetEntrantsAsync(long id) => _db.GetGiveawayEntrantsAsync(id);
        public Task<List<(string Time, string Kind, string Message)>> GetActivityAsync(long id)
            => _db.GetGiveawayActivityAsync(id);
        public Task<long?> GetDefaultIdAsync() => _db.GetDefaultGiveawayIdAsync();

        // ── Create ──────────────────────────────────────────────────────────
        public async Task<Giveaway> CreateAsync(string title, string openedBy)
        {
            title = string.IsNullOrWhiteSpace(title) ? "Untitled giveaway" : title.Trim();
            string baseKey = $"g-{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

            // Pick the first free per-day suffix (g-2026-05-27-a, -b, …) so the
            // human-facing key reads cleanly; fall back to a guid tail in the
            // (practically impossible) event all 26 letters are taken in a day.
            var existing = await _db.GetGiveawaysAsync().ConfigureAwait(false);
            var used = new HashSet<string>(
                existing.Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
            string key = $"{baseKey}-{Guid.NewGuid():N}".Substring(0, baseKey.Length + 7);
            for (char c = 'a'; c <= 'z'; c++)
            {
                string candidate = $"{baseKey}-{c}";
                if (!used.Contains(candidate)) { key = candidate; break; }
            }

            long id = await _db.CreateGiveawayAsync(key, title, string.IsNullOrWhiteSpace(openedBy) ? "broadcaster" : openedBy, NowIso())
                .ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF", $"giveaway created — \"{title}\"", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();

            // GetGiveawayAsync is genuinely nullable — a
            // failed re-read (row gone, transient DB error) would NRE on the old `!`.
            // Guard it: if we can't read back the giveaway we just created, surface a
            // clear failure instead of dereferencing null downstream.
            var created = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (created is null)
                throw new InvalidOperationException(
                    $"GiveawayService.CreateAsync: giveaway #{id} ('{key}') was created but could not be re-read from the databank.");
            return created;
        }

        // ── Close ───────────────────────────────────────────────────────────
        /// <summary>Closes a giveaway; returns (total tickets, entrant count).</summary>
        public async Task<(int Total, int Entrants)> CloseAsync(long id)
        {
            var totals = await _db.GetGiveawayTotalsAsync(id).ConfigureAwait(false);
            await _db.SetGiveawayStatusAsync(id, "closed", NowIso()).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF",
                $"giveaway closed — {totals.Total} tickets, {totals.Entrants} entrants", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
            return totals;
        }

        // ── Ticket entry ────────────────────────────────────────────────────
        /// <summary>
        /// Adds <paramref name="increment"/> tickets for a user, clamped to the
        /// per-user cap; returns the user's new total plus whether the per-user
        /// cap truncated the entry (Capped = the increment could not be applied
        /// in full — including "already at/over the cap"). The subscriber bonus
        /// is NOT applied here — it is a ticket-weight factor resolved at draw
        /// time (see DrawWinnerAsync); the role is only recorded for the badge
        /// and as the draw-time fallback. When the giveaway is not open, this
        /// is read-only — it returns the current count without adding (the
        /// spec's "same node can also just display tickets") and is never
        /// Capped; neither is increment &lt;= 0.
        /// </summary>
        public async Task<(int Tickets, bool Capped)> AddTicketAsync(long id, string username, string role, int increment, bool isMod = false)
        {
            if (string.IsNullOrWhiteSpace(username)) return (0, false);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null) return (0, false);

            // Read-only outside the open state (also covers increment <= 0).
            if (!string.Equals(g.Status, "open", StringComparison.OrdinalIgnoreCase) || increment <= 0)
                return (await _db.GetTicketsForUserAsync(id, username).ConfigureAwait(false), false);

            var (newTotal, capped) = await _db.UpsertTicketAsync(id, username,
                string.IsNullOrWhiteSpace(role) ? "viewer" : role, increment, g.CapPerUser, NowIso(), isMod).ConfigureAwait(false);

            string capNote = capped ? " · cap reached" : "";
            await _db.LogGiveawayActivityAsync(id, "INF",
                $"{username} entered  +{increment} ticket{(increment == 1 ? "" : "s")}  ({newTotal} total{capNote})", NowIso())
                .ConfigureAwait(false);
            RaiseEntrants(id);
            return (newTotal, capped);
        }

        // ── Per-user cap ────────────────────────────────────────────────────
        /// <summary>
        /// Sets the per-user ticket cap (0 = unlimited; negatives clamp to 0).
        /// Numeric-range validation only — the cap gates future increments, it
        /// never trims counts users already hold.
        /// </summary>
        public async Task SetCapPerUserAsync(long id, int capPerUser)
        {
            capPerUser = Math.Max(0, capPerUser);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null || g.CapPerUser == capPerUser) return;

            await _db.SetGiveawayCapPerUserAsync(id, capPerUser).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF",
                capPerUser == 0
                    ? "max tickets per user removed — unlimited"
                    : $"max tickets per user set to {capPerUser}", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
        }

        // ── Ticket price ────────────────────────────────────────────────────
        /// <summary>
        /// Sets the channel-point price per ticket (0 = free; negatives clamp
        /// to 0). Charged by Giveaway.Ticket nodes that carry a currency table
        /// and follow this giveaway's settings (Public = true).
        /// </summary>
        public async Task SetTicketPriceAsync(long id, int price)
        {
            price = Math.Max(0, price);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null || g.TicketPrice == price) return;

            await _db.SetGiveawayTicketPriceAsync(id, price).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF",
                price == 0
                    ? "ticket price removed — entry is free"
                    : $"ticket price set to {price} channel point{(price == 1 ? "" : "s")} per ticket",
                NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
        }

        // ── Priced ticket entry (Giveaway.Ticket with a currency table) ─────
        /// <summary>
        /// Ticket entry that can charge channel points from a user table
        /// (columns <c>name</c> / <c>currency</c>). Price resolution follows
        /// the node's Public toggle: Public = true → the resolved giveaway's
        /// TicketPrice setting; Public = false → the node's own Price pill
        /// (<paramref name="nodePrice"/>). <paramref name="isAll"/> grants the
        /// maximum the balance and the per-user cap allow instead of the fixed
        /// increment. Closed giveaways stay read-only (never charged), same as
        /// <see cref="AddTicketAsync"/>. Returns the user's new total, how many
        /// tickets this call granted, and which gate (cap / funds) blocked it.
        /// </summary>
        public async Task<(int Tickets, int Purchased, bool Capped, bool NoFunds)> PurchaseTicketAsync(
            long id, string username, string role, int increment, bool isAll,
            bool isPublic, int nodePrice, string currencyTable, bool isMod = false)
        {
            if (string.IsNullOrWhiteSpace(username)) return (0, 0, false, false);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null) return (0, 0, false, false);

            // Read-only outside the open state; a non-IsAll increment <= 0 is
            // the "just display the count" probe. Never charged.
            if (!string.Equals(g.Status, "open", StringComparison.OrdinalIgnoreCase)
                || (!isAll && increment <= 0))
                return (await _db.GetTicketsForUserAsync(id, username).ConfigureAwait(false), 0, false, false);

            int price = Math.Max(0, isPublic ? g.TicketPrice : nodePrice);
            string table = (currencyTable ?? "").Trim();
            if (table.Length == 0) price = 0;   // nowhere to charge from → free entry

            var r = await _db.PurchaseGiveawayTicketsAsync(
                id, username, string.IsNullOrWhiteSpace(role) ? "viewer" : role,
                increment, g.CapPerUser, price, table, isAll, NowIso(), isMod).ConfigureAwait(false);

            if (r.TableMissing)
            {
                GlobalLogger.Log(
                    $"Giveaway ticket purchase: currency table '{table}' is missing (or lacks the " +
                    "name/currency columns) — entry blocked. Pick the table on the Giveaway.Ticket " +
                    "node or use its \"Create 'ChannelPoints' table\" item.",
                    "Script", LogLevel.CriticalError);
                await _db.LogGiveawayActivityAsync(id, "INF",
                    $"{username} entry blocked — currency table \"{table}\" not found", NowIso()).ConfigureAwait(false);
                // Raised for the same reason as the NoFunds branch: the page's
                // activity feed only reloads on an event, and the streamer
                // needs to SEE that entries are being blocked.
                RaiseEntrants(id);
                // Script-visible as the funds gate: nothing was granted.
                return (r.Tickets, 0, false, true);
            }

            if (r.NoFunds)
            {
                await _db.LogGiveawayActivityAsync(id, "INF",
                    $"{username} entry blocked — not enough channel points (has {r.Balance})", NowIso()).ConfigureAwait(false);
                RaiseEntrants(id);
                return (r.Tickets, 0, false, true);
            }

            if (r.Purchased > 0)
            {
                string capNote = r.Capped ? " · cap reached" : "";
                string costNote = r.Charged > 0 ? $" for {r.Charged} points" : "";
                await _db.LogGiveawayActivityAsync(id, "INF",
                    $"{username} entered  +{r.Purchased} ticket{(r.Purchased == 1 ? "" : "s")}{costNote}  ({r.Tickets} total{capNote})",
                    NowIso()).ConfigureAwait(false);
                RaiseEntrants(id);
            }
            else if (r.Capped)
            {
                await _db.LogGiveawayActivityAsync(id, "INF",
                    $"{username} entered  +0 tickets  ({r.Tickets} total · cap reached)", NowIso()).ConfigureAwait(false);
                RaiseEntrants(id);
            }

            return (r.Tickets, r.Purchased, r.Capped, false);
        }

        private static bool IsSubscriber(string? role) =>
            string.Equals(role, "sub", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "subscriber", StringComparison.OrdinalIgnoreCase);

        // The draw's mod check honors the dedicated entry flag AND the legacy
        // single-badge form (entries recorded before the IsSub/IsMod rework
        // could only carry "mod" in the Role column).
        internal static bool IsModerator(GiveawayEntrant e) =>
            e.IsMod ||
            string.Equals(e.Role, "mod", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Role, "moderator", StringComparison.OrdinalIgnoreCase);

        // ── Subscriber-bonus factor ─────────────────────────────────────────
        /// <summary>
        /// Sets the subscriber ticket-weight factor. 1 = no bonus; a subscriber's
        /// tickets count ×factor in the weighted draw. Clamped to [1, 100] —
        /// a factor below 1 would penalize subscribers, which is not a bonus.
        /// </summary>
        public async Task SetSubscriberBonusFactorAsync(long id, double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor)) factor = 1.0;
            factor = Math.Clamp(factor, 1.0, 100.0);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null || Math.Abs(g.SubscriberBonusFactor - factor) < 0.0001) return;

            await _db.SetGiveawaySubBonusFactorAsync(id, factor).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF",
                factor <= 1.0
                    ? "subscriber bonus removed — all tickets weigh the same"
                    : $"subscriber bonus set to ×{factor.ToString("0.##", CultureInfo.InvariantCulture)} ticket weight at draw",
                NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
        }

        // ── Moderator-bonus factor ──────────────────────────────────────────
        /// <summary>
        /// Sets the moderator ticket-weight factor. 1 = no bonus; a moderator's
        /// tickets count ×factor in the weighted draw (multiplies with the
        /// subscriber factor for entrants who are both). Same [1, 100] clamp
        /// as the subscriber factor.
        /// </summary>
        public async Task SetModeratorBonusFactorAsync(long id, double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor)) factor = 1.0;
            factor = Math.Clamp(factor, 1.0, 100.0);
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null || Math.Abs(g.ModBonusFactor - factor) < 0.0001) return;

            await _db.SetGiveawayModBonusFactorAsync(id, factor).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "INF",
                factor <= 1.0
                    ? "moderator bonus removed — all tickets weigh the same"
                    : $"moderator bonus set to ×{factor.ToString("0.##", CultureInfo.InvariantCulture)} ticket weight at draw",
                NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
        }

        // ── Weighted winner draw ────────────────────────────────────────────
        /// <summary>
        /// Draws a winner weighted by ticket count (cumulative-weight / CDF
        /// method: roll r in [0,totalWeight), walk the running sum, first
        /// entrant whose cumulative weight exceeds r wins — odds are exactly
        /// proportional to weight). When the giveaway carries a subscriber
        /// bonus factor &gt; 1, a pre-draw sequence gathers every entrant's
        /// CURRENT subscription status from Streamer.bot (via the resolver the
        /// ScriptManager wires in) and a subscriber's tickets weigh ×factor;
        /// entrants the lookup can't resolve fall back to the role recorded at
        /// entry. Returns null when the pool is empty. Records the winner and
        /// flips the giveaway to "drawn".
        /// </summary>
        // Draws are serialized: with a sub-bonus factor the pre-draw
        // Streamer.bot gather turns the once-instant critical section into a
        // multi-second window — an ungated second call (double-click, a
        // giveaway.winner node firing while the panel draws) would read the
        // same pool concurrently and append two winners.
        private readonly SemaphoreSlim _drawGate = new(1, 1);

        public async Task<(string Name, int Tickets, int OddsOneIn)?> DrawWinnerAsync(long id)
        {
            await _drawGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var entrants = await _db.GetGiveawayEntrantsAsync(id).ConfigureAwait(false);
                if (entrants.Count == 0 || entrants.Sum(e => e.Tickets) <= 0) return null;

                var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
                double factor = g?.SubscriberBonusFactor ?? 1.0;
                double modFactor = g?.ModBonusFactor ?? 1.0;

                var subs = factor > 1.0
                    ? await ResolveSubscribersForDrawAsync(id, entrants, factor).ConfigureAwait(false)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Mod status comes from the flags recorded at entry (IsMod
                // column / legacy "mod" badge) — no live gather; mod rosters
                // barely churn compared to subscriptions.
                var mods = modFactor > 1.0
                    ? new HashSet<string>(
                        entrants.Where(IsModerator).Select(e => e.Username),
                        StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var pool = BuildWeightedPool(entrants, subs, factor, mods, modFactor);
                double totalWeight = pool.Sum(p => p.Weight);
                if (totalWeight <= 0) return null;

                var winner = PickWeighted(pool, Random.Shared.NextDouble() * totalWeight);

                // Odds come from the WEIGHTED pool (a sub's/mod's factor
                // improves them), so the panel overlay and the activity line agree.
                double winnerWeight = pool.First(p => ReferenceEquals(p.Entrant, winner)).Weight;
                int odds = winnerWeight > 0 ? Math.Max(1, (int)Math.Round(totalWeight / winnerWeight)) : entrants.Count;
                string subNote = factor > 1.0 && subs.Contains(winner.Username)
                    ? $" · sub ×{factor.ToString("0.##", CultureInfo.InvariantCulture)}"
                    : "";
                string modNote = modFactor > 1.0 && mods.Contains(winner.Username)
                    ? $" · mod ×{modFactor.ToString("0.##", CultureInfo.InvariantCulture)}"
                    : "";
                await _db.AppendGiveawayWinnerAsync(id, winner.Username).ConfigureAwait(false);
                await _db.LogGiveawayActivityAsync(id, "WIN",
                    $"{winner.Username} won — {winner.Tickets} tickets{subNote}{modNote} · 1 in {odds}", NowIso()).ConfigureAwait(false);
                RaiseGiveaways();
                return (winner.Username, winner.Tickets, odds);
            }
            finally
            {
                _drawGate.Release();
            }
        }

        /// <summary>
        /// The pre-draw gather: asks Streamer.bot (via the wired resolver) for
        /// every entrant's current subscription status, persists role changes
        /// back onto the ticket rows (viewer↔sub only — broadcaster/mod/vip
        /// badges are never downgraded by a sub check), and returns the set of
        /// subscriber usernames. Entrants the lookup can't resolve keep their
        /// recorded role as the answer.
        /// </summary>
        private async Task<HashSet<string>> ResolveSubscribersForDrawAsync(
            long id, List<GiveawayEntrant> entrants, double factor)
        {
            var subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyDictionary<string, bool>? resolved = null;
            var resolver = SubscriberStatusResolver;
            if (resolver is not null)
            {
                await _db.LogGiveawayActivityAsync(id, "INF",
                    $"draw prep — checking subscriber status of {entrants.Count} entrant{(entrants.Count == 1 ? "" : "s")} via Streamer.bot…",
                    NowIso()).ConfigureAwait(false);
                try { resolved = await resolver(entrants.Select(e => e.Username).ToList()).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    GlobalLogger.Error("GiveawayService", "Pre-draw subscriber lookup failed", ex);
                }
            }

            var roleUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int verified = 0;
            foreach (var e in entrants)
            {
                bool isSub;
                if (resolved is not null && resolved.TryGetValue(e.Username, out bool live))
                {
                    isSub = live;
                    verified++;
                    // Persist the fresh status, but only across the viewer↔sub
                    // axis — a recorded broadcaster/mod/vip badge outranks it.
                    if (live && !IsSubscriber(e.Role) &&
                        string.Equals(e.Role, "viewer", StringComparison.OrdinalIgnoreCase))
                        roleUpdates[e.Username] = "sub";
                    else if (!live && IsSubscriber(e.Role))
                        roleUpdates[e.Username] = "viewer";
                }
                else
                {
                    isSub = IsSubscriber(e.Role); // fallback: role recorded at entry
                }
                if (isSub) subs.Add(e.Username);
            }

            if (roleUpdates.Count > 0)
            {
                await _db.UpdateGiveawayEntrantRolesAsync(id, roleUpdates).ConfigureAwait(false);
                RaiseEntrants(id);
            }

            string sourceNote = resolved is null
                ? "Streamer.bot unavailable — using roles recorded at entry"
                : $"{verified} of {entrants.Count} verified live";
            await _db.LogGiveawayActivityAsync(id, "INF",
                $"draw prep — {subs.Count} subscriber{(subs.Count == 1 ? "" : "s")} weigh ×{factor.ToString("0.##", CultureInfo.InvariantCulture)} ({sourceNote})",
                NowIso()).ConfigureAwait(false);
            return subs;
        }

        // Weight math split out so the draw odds are unit-testable without
        // fighting Random.Shared: pool building applies the sub + mod factors
        // (multiplicative for an entrant who is both), the CDF walk picks for
        // a given roll in [0, totalWeight).
        internal static List<(GiveawayEntrant Entrant, double Weight)> BuildWeightedPool(
            IReadOnlyList<GiveawayEntrant> entrants, IReadOnlySet<string> subs, double factor,
            IReadOnlySet<string>? mods = null, double modFactor = 1.0)
            => entrants
                .Select(e => (Entrant: e, Weight: e.Tickets
                    * (factor > 1.0 && subs.Contains(e.Username) ? factor : 1.0)
                    * (modFactor > 1.0 && mods is not null && mods.Contains(e.Username) ? modFactor : 1.0)))
                .ToList();

        internal static GiveawayEntrant PickWeighted(
            IReadOnlyList<(GiveawayEntrant Entrant, double Weight)> pool, double roll)
        {
            double cumulative = 0;
            GiveawayEntrant winner = pool[pool.Count - 1].Entrant;
            foreach (var (entrant, weight) in pool)
            {
                cumulative += weight;
                if (roll < cumulative) { winner = entrant; break; }
            }
            return winner;
        }

        // ── Default giveaway ────────────────────────────────────────────────
        public async Task SetDefaultAsync(long id, bool isDefault)
        {
            await _db.SetDefaultGiveawayAsync(id, isDefault).ConfigureAwait(false);
            if (isDefault)
                await _db.LogGiveawayActivityAsync(id, "INF", "marked as default giveaway", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
        }

        /// <summary>
        /// True while the giveaway exists and its Status is "open" (accepting
        /// entries). Backs the giveaway.is_active probe (Giveaway.IsActive
        /// node); closed / drawn / missing all read false.
        /// </summary>
        public async Task<bool> IsOpenAsync(long id)
        {
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            return g is not null && string.Equals(g.Status, "open", StringComparison.OrdinalIgnoreCase);
        }

        // ── Target resolution for the script commands ───────────────────────
        /// <summary>
        /// Resolves which giveaway a public-aware command targets. When
        /// <paramref name="isPublic"/> is true the node follows the app-wide
        /// default giveaway (the interface's "set default" control); otherwise
        /// it uses its own selector, matched by numeric id, then key, then title.
        /// Returns null when nothing matches.
        /// </summary>
        public async Task<long?> ResolveTargetAsync(string giveawaySelector, bool isPublic)
        {
            if (isPublic)
            {
                var def = await _db.GetDefaultGiveawayIdAsync().ConfigureAwait(false);
                if (def.HasValue) return def;
                // No default set yet → fall through to the node's own selector.
            }

            if (string.IsNullOrWhiteSpace(giveawaySelector)) return null;
            giveawaySelector = giveawaySelector.Trim();

            if (long.TryParse(giveawaySelector, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numericId))
            {
                var byId = await _db.GetGiveawayAsync(numericId).ConfigureAwait(false);
                if (byId is not null) return byId.Id;
            }

            var byKey = await _db.GetGiveawayByKeyAsync(giveawaySelector).ConfigureAwait(false);
            if (byKey is not null) return byKey.Id;

            var all = await _db.GetGiveawaysAsync().ConfigureAwait(false);
            var byTitle = all.FirstOrDefault(g =>
                string.Equals(g.Title, giveawaySelector, StringComparison.OrdinalIgnoreCase));
            return byTitle?.Id;
        }
    }
}
