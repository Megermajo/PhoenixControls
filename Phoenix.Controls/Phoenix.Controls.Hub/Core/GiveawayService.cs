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
    // rules (slug generation, subscriber bonus, per-user cap, the weighted
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

            // [P1 swarm-audit 2026-05-29] GetGiveawayAsync is genuinely nullable — a
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
        /// Adds <paramref name="increment"/> tickets for a user (plus the
        /// giveaway's subscriber bonus when the role is a subscriber), clamped
        /// to the per-user cap; returns the user's new total. When the giveaway
        /// is not open, this is read-only — it returns the current count without
        /// adding (the spec's "same node can also just display tickets").
        /// </summary>
        public async Task<int> AddTicketAsync(long id, string username, string role, int increment)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            var g = await _db.GetGiveawayAsync(id).ConfigureAwait(false);
            if (g is null) return 0;

            // Read-only outside the open state (also covers increment <= 0).
            if (!string.Equals(g.Status, "open", StringComparison.OrdinalIgnoreCase) || increment <= 0)
                return await _db.GetTicketsForUserAsync(id, username).ConfigureAwait(false);

            int inc = increment;
            bool subBonusApplied = IsSubscriber(role) && g.SubscriberBonus > 0;
            if (subBonusApplied) inc += g.SubscriberBonus;

            int newTotal = await _db.UpsertTicketAsync(id, username,
                string.IsNullOrWhiteSpace(role) ? "viewer" : role, inc, g.CapPerUser, NowIso()).ConfigureAwait(false);

            string bonusNote = subBonusApplied ? " · subscriber bonus" : "";
            await _db.LogGiveawayActivityAsync(id, "INF",
                $"{username} entered  +{inc} ticket{(inc == 1 ? "" : "s")}  ({newTotal} total{bonusNote})", NowIso())
                .ConfigureAwait(false);
            RaiseEntrants(id);
            return newTotal;
        }

        private static bool IsSubscriber(string? role) =>
            string.Equals(role, "sub", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "subscriber", StringComparison.OrdinalIgnoreCase);

        // ── Weighted winner draw ────────────────────────────────────────────
        /// <summary>
        /// Draws a winner weighted by ticket count (cumulative-weight / CDF
        /// method: roll r in [0,total), walk the running sum, first entrant whose
        /// cumulative total exceeds r wins — odds are exactly proportional to
        /// tickets). Returns null when the pool is empty. Records the winner and
        /// flips the giveaway to "drawn".
        /// </summary>
        public async Task<(string Name, int Tickets)?> DrawWinnerAsync(long id)
        {
            var entrants = await _db.GetGiveawayEntrantsAsync(id).ConfigureAwait(false);
            int total = entrants.Sum(e => e.Tickets);
            if (total <= 0 || entrants.Count == 0) return null;

            int roll = Random.Shared.Next(total); // [0, total)
            int cumulative = 0;
            GiveawayEntrant winner = entrants[entrants.Count - 1];
            foreach (var e in entrants)
            {
                cumulative += e.Tickets;
                if (roll < cumulative) { winner = e; break; }
            }

            int odds = winner.Tickets > 0 ? (int)Math.Round((double)total / winner.Tickets) : total;
            await _db.AppendGiveawayWinnerAsync(id, winner.Username).ConfigureAwait(false);
            await _db.LogGiveawayActivityAsync(id, "WIN",
                $"{winner.Username} won — {winner.Tickets} tickets · 1 in {odds}", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
            return (winner.Username, winner.Tickets);
        }

        // ── Default giveaway ────────────────────────────────────────────────
        public async Task SetDefaultAsync(long id, bool isDefault)
        {
            await _db.SetDefaultGiveawayAsync(id, isDefault).ConfigureAwait(false);
            if (isDefault)
                await _db.LogGiveawayActivityAsync(id, "INF", "marked as default giveaway", NowIso()).ConfigureAwait(false);
            RaiseGiveaways();
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
