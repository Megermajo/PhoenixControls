using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// The donation/tip INGESTION BOUNDARY policy — everything that must happen once
    /// per inbound money event, before it fans out to Timer / Loyalty / Alerts / scripts.
    ///
    /// It lives here rather than inside any one tool because
    /// <c>ScriptManager.ExecuteGenericEventAsync</c> feeds the SAME event to four
    /// independent consumers, each of which builds its own var dictionary. De-duping
    /// inside Loyalty alone would still let Timer add subathon seconds twice and Alerts
    /// fire twice for one donation.
    ///
    /// Three jobs:
    ///   1. <see cref="ShouldSuppress"/> — test-event and duplicate rejection.
    ///   2. <see cref="NormalizeMoneyVars"/> — canonical money (see <see cref="DonationAmount"/>),
    ///      including the charity CAMPAIGN pair (amount + target) under ONE scale verdict.
    ///   3. A spoof-plausibility warning, mirroring the Twitch.Raid viewer-count check.
    /// </summary>
    internal static class DonationIngest
    {
        // ── Which sources are money sources ──────────────────────────────────
        // Verified against a LIVE Streamer.bot 1.0.4 GetEvents roster (2026-08-01),
        // NOT against documentation. Two names differ from every doc/report that
        // preceded this: the source is "Kofi" (not "Ko-Fi") and "Pallygg" (not
        // "Pally.gg"). Two further sources that prior planning listed — Throne and
        // StreamLoots — DO NOT EXIST on the SB 1.0.4 event surface at all and are
        // deliberately absent here; adding them would have been permanently dead
        // config with no error.
        private static readonly HashSet<string> DonationSources = new(StringComparer.OrdinalIgnoreCase)
        {
            "Streamlabs", "StreamElements", "Kofi", "Patreon", "TipeeeStream",
            "TreatStream", "DonorDrive", "Fourthwall", "Pallygg", "Shopify",
        };

        /// <summary>True when the event type's source is a third-party money broker.</summary>
        internal static bool IsDonationEvent(string eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return false;
            int dot = eventType.IndexOf('.');
            if (dot <= 0) return false;
            return DonationSources.Contains(eventType.Substring(0, dot));
        }

        // Twitch's own charity events carry real money and must ride the SAME
        // canonical normalisation as broker tips — otherwise a charity donation
        // would reach alert text as a floored int while a Ko-Fi tip reached it as
        // a decimal, which is exactly the split this sprint exists to remove.
        // They are NOT in DonationSources because they are not third-party
        // brokers: they need the money path but not the cross-broker de-dupe.
        private static readonly HashSet<string> TwitchMoneyEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            "Twitch.CharityDonation", "Twitch.CharityStarted",
            "Twitch.CharityProgress", "Twitch.CharityCompleted",
        };

        /// <summary>True when the event carries a real-money amount of any origin.</summary>
        internal static bool IsMoneyEvent(string eventType)
            => IsDonationEvent(eventType) || TwitchMoneyEvents.Contains(eventType ?? "");

        // ── Probe candidates ─────────────────────────────────────────────────
        // Streamer.bot publishes NO WebSocket schema for any third-party
        // integration (every broker page reads "No Schema Available"), so each
        // field is probed against a candidate list in priority order and the real
        // field names are printed once per event type on a miss (see
        // NoteProbeMissOnce) — turning a wrong guess into a one-line fix from a
        // single live capture rather than a silent zero.
        //
        // ★ TWO SURFACES, AND THEY DISAGREE. Streamer.bot documents per-broker
        // field names for its TRIGGER-VARIABLE surface (flat keys, nesting encoded
        // as dots inside the key: "charity.amount.value"). The WEBSOCKET surface —
        // the one this code consumes — uses real nested objects and, where both
        // are documented, uses DIFFERENT names (WatchStreak is `watchStreak` on the
        // trigger surface and `streak_count` on the wire). So each list below
        // carries the documented trigger-surface name AND a generic fallback, and
        // ProbeString resolves dotted candidates three ways: nested walk, flat
        // dotted key, then plain key.
        //
        // ★ Three names are VERBATIM TYPOS in Streamer.bot itself. They are
        // reproduced exactly, with the corrected spelling kept as a fallback in
        // case SB ever fixes them: `tipProccessingFee` (double-c, Pally),
        // `fw.statmessageus` (a corrupted status+message, Fourthwall) and `fw.Id`
        // (capital I on GiftPurchase only, lowercase everywhere else).
        private static readonly string[] AmountProbes =
        {
            // Broker-specific, most distinctive first.
            "donationAmount",                          // Streamlabs Donation
            "charityDonationAmount",                   // Streamlabs CharityDonation
            "tipAmount",                               // StreamElements Tip
            "merchAmount",                             // StreamElements Merch
            "donorAmount",                             // DonorDrive
            "tipNetAmountInCents", "tipNetAmount",     // Pally.gg (int cents, then decimal)
            "attributes.currently_entitled_amount_cents", // Patreon (integer US cents)
            "fw.amount", "fw.total",                   // Fourthwall
            "shopify.total_price",                     // Shopify
            "charity.amount.value", "charity.currentAmount.value", // Twitch charity
            // Generic.
            "amount", "value", "total", "totalAmount", "grossAmount",
            "amountInCents", "formattedAmount",
        };
        private static readonly string[] CurrencyProbes =
        {
            "donationCurrency", "charityDonationCurrency", "tipCurrency", "merchCurrency",
            "fw.currency", "shopify.currency",
            "charity.amount.currency", "charity.currentAmount.currency",
            "currency", "currencyCode", "currency_code",
        };
        private static readonly string[] TxIdProbes =
        {
            // Only six of the ten brokers expose a stable per-transaction id at
            // all; the other four rely entirely on the windowed layer-2 collapse.
            "messageId",                 // Ko-Fi (all four of its money events)
            "tipeeStreamId",             // TipeeeStream
            "tipId",                     // Pally.gg
            "fw.donationId", "fw.orderId", "fw.Id", "fw.id",  // Fourthwall (note the capital-I typo)
            "shopify.id",                // Shopify
            "transactionId", "transaction_id", "paymentId", "orderId", "donationId", "eventId", "id",
        };

        /// <summary>
        /// <see cref="TxIdProbes"/> minus the bare generic <c>"id"</c>, used for events
        /// that describe a RECURRING relationship rather than one payment.
        ///
        /// <para>★ On a subscription-shaped broker event, <c>id</c> is the id of the
        /// SUBSCRIPTION, not of this month's charge — Patreon's <c>PledgeUpdated</c>
        /// carries the same pledge id every month. Layer 1 has no time expiry by design
        /// (a genuine replay must be caught however late it arrives), so month 2 at the
        /// same amount hashed to month 1's key and was dropped for the life of the Hub
        /// process: recurring income silently stopped paying points and stopped
        /// extending the subathon, with no error anywhere.</para>
        ///
        /// <para>A TTL on layer 1 was the alternative and was rejected: it buys this
        /// back by weakening replay protection for every broker to solve a problem only
        /// the recurring ones have. Dropping the wrong probe is the narrower cut —
        /// these events keep every SPECIFIC id probe above and merely stop treating a
        /// relationship id as a transaction id. A broker exposing no per-charge id at
        /// all falls through to layer 2, exactly like the four id-less brokers
        /// already do.</para>
        /// </summary>
        private static readonly string[] RecurringTxIdProbes =
            Array.FindAll(TxIdProbes, p => !string.Equals(p, "id", StringComparison.Ordinal));

        /// <summary>
        /// Leaf fragments marking a recurring-relationship money event. Matched against
        /// the event TYPE, which is Phoenix's own normalised <c>Source.Leaf</c> string,
        /// so this is a closed vocabulary and not broker-supplied free text.
        /// </summary>
        private static readonly string[] RecurringLeafMarkers =
        {
            "pledge",          // Patreon: PledgeCreated / PledgeUpdated / PledgeDeleted
            "subscription",    // Ko-Fi Subscription, Fourthwall SubscriptionPurchased
            "resubscription",
            "membership",
        };

        private static readonly string[] TestProbes =
        {
            // Documented and first-class on EVERY Twitch WebSocket schema; NOT
            // documented on any of the twenty broker events, so this resolves
            // false for brokers until one of them proves otherwise.
            "isTest", "is_test", "test", "isTestMode", "testMode",
        };
        private static readonly string[] DonorProbes =
        {
            "donationFrom", "charityDonationFrom", "merchandiseFrom",  // Streamlabs
            "tipUsername", "merchUsername",                            // StreamElements
            "donorName",                                               // DonorDrive
            "tipDisplayName",                                          // Pally.gg
            "fw.username", "fw.nickname",                              // Fourthwall (nickname on SubscriptionPurchased)
            "shopify.name",                                            // Shopify
            "user.Attributes.full_name",                               // Patreon
            "from", "username", "name", "displayName", "user", "fromName", "supporterName",
        };

        /// <summary>
        /// Donor labels that are NOT an identity — what a broker sends when the supporter
        /// stayed anonymous or left the name blank. Two unrelated people share the
        /// string, which is precisely what makes it useless as half of a de-dupe key.
        ///
        /// <para>★ Only labels a broker actually emits for a WITHHELD identity belong
        /// here. "someone", "guest" and "unknown" were removed on purpose: all three are
        /// registerable display names on every broker in the probe list, and classifying
        /// a real donor called Guest as anonymous exempts their tips from the
        /// cross-broker collapse — one relayed tip, paid out twice. The reverse mistake
        /// is far cheaper: a genuinely-anonymous label missing from this list only risks
        /// the narrow windowed collapse of two identical-amount tips, and no broker is
        /// known to emit those three strings for anonymity in the first place.</para>
        /// </summary>
        private static readonly string[] AnonymousDonorLabels =
        {
            "anonymous", "anon", "anonymous user", "an anonymous user",
            "n/a", "-",
        };

        /// <summary>True when a donor string cannot identify a person.</summary>
        private static bool IsAnonymousDonor(string donor)
        {
            string d = (donor ?? "").Trim();
            if (d.Length == 0) return true;

            foreach (string label in AnonymousDonorLabels)
                if (d.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// True when <paramref name="eventType"/> describes a recurring relationship, so
        /// a bare <c>"id"</c> on it identifies the subscription rather than the payment.
        /// Substring-matched on the LEAF so a future broker naming its event
        /// <c>…MembershipRenewed</c> inherits the protection without a code change.
        /// </summary>
        private static bool IsRecurringShape(string eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return false;

            int dot = eventType.LastIndexOf('.');
            string leaf = dot >= 0 && dot + 1 < eventType.Length ? eventType[(dot + 1)..] : eventType;

            foreach (string marker in RecurringLeafMarkers)
                if (leaf.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        // ── Duplicate suppression ────────────────────────────────────────────
        // TWO LAYERS, because they solve two different problems and neither alone
        // is sufficient:
        //
        //  LAYER 1 — exact idempotency. Key = source + broker transaction id. This is
        //  the reliable one: it kills a genuine REPLAY of the same broker event (an SB
        //  restart re-emitting, a reconnect re-delivering a backlog). No time window
        //  gates the CHECK — but the entries are not literally immortal: Remember()'s
        //  cap sweep evicts >2-minute entries from BOTH maps once DedupeMaxKeys is
        //  reached, so an arbitrarily delayed replay is only caught while its entry
        //  has survived that pressure. Bounded memory beats a perfect ledger here.
        //
        //  ★ The amount is deliberately NOT in this key. It used to be, described as a
        //  "tamper check", but putting it there inverted the guarantee: a replay whose
        //  amount had been altered hashed to a DIFFERENT key and sailed through as a
        //  second donation — i.e. the one manipulation worth defending against was the
        //  one that defeated idempotency. The transaction id alone decides identity;
        //  a mismatching amount on a known id is now a once-per-transaction logged
        //  warning (below) instead of a fresh payout.
        //
        //  ★ On a recurring event (Patreon pledge, Ko-Fi subscription) a bare "id" is
        //  the SUBSCRIPTION's id, not this charge's — see RecurringTxIdProbes.
        //
        //  LAYER 2 — cross-broker collapse, windowed. When the streamer has BOTH a
        //  broker connected directly AND another relaying it (the documented Ko-Fi
        //  surfaced by Ko-Fi *and* StreamElements case), the two copies arrive under
        //  DIFFERENT sources with DIFFERENT transaction ids — brokers do not share an
        //  id space. Layer 1 structurally cannot see them as the same donation. The
        //  only remaining signal is (donor, amount, currency), which is necessarily
        //  windowed.
        //
        //  ★ HONEST LIMIT, worth stating plainly: because relative relay delay is
        //  unbounded, layer 2 is a heuristic and cannot be made exact. The window is
        //  generous (2 min) and the key is narrow (same donor AND same exact minor
        //  units AND same currency), so a false collapse requires one person donating
        //  the identical amount twice inside the window. The alternative — no layer 2
        //  — means paying out twice for one donation, which is the worse failure.
        //
        //  ★ …EXCEPT for anonymous donors, where that premise is simply false. Brokers
        //  send "Anonymous" (or nothing) for a large share of real tips, so the donor
        //  half of the key stops discriminating between PEOPLE and two unrelated
        //  anonymous tips of the same amount inside the window collapse into one — a
        //  real donation dropped before Timer, Loyalty and Alerts ever see it, which is
        //  the failure this heuristic was supposed to be trading against. Layer 2
        //  therefore skips anonymous donors entirely (IsAnonymousDonor): named donors
        //  keep the cross-broker protection, anonymous ones fall back to layer 1's
        //  transaction id where the broker supplies one, and to nothing where it does
        //  not. Double-paying an anonymous relayed tip is the accepted cost, chosen
        //  over silently eating a real one.
        private const int DedupeMaxKeys = 512;
        private const long CrossBrokerWindowMs = 120_000;

        /// <summary>
        /// One remembered donation. <paramref name="MinorUnits"/> is carried so layer 1
        /// can REPORT an amount that changed under a known transaction id without letting
        /// that change create a second payout; <see cref="UnknownMinorUnits"/> marks an
        /// event whose amount could not be resolved, so an unresolvable amount is never
        /// reported as a mismatch against a real one.
        /// </summary>
        private readonly record struct SeenEntry(long AtMs, long MinorUnits, bool MismatchWarned = false);

        private const long UnknownMinorUnits = long.MinValue;

        private static readonly object _gate = new();
        private static readonly Dictionary<string, SeenEntry> _seenExact = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, SeenEntry> _seenFuzzy = new(StringComparer.Ordinal);

        /// <summary>
        /// Decides whether an inbound donation event must NOT reach the tool fan-out.
        /// Returns true to drop. Never throws — an ingestion-policy fault must not
        /// break event dispatch.
        /// </summary>
        internal static bool ShouldSuppress(string eventType, JsonElement data, out string reason)
        {
            reason = "";
            try
            {
                if (!IsDonationEvent(eventType)) return false;

                // ── Test events ──────────────────────────────────────────────
                // Brokers fire test donations from their own dashboards, and SB's
                // Test Trigger UI can synthesise any payload. Default to IGNORING
                // them: a test tip must not move real points, real subathon time or
                // a real alert. Opt-in for streamers who deliberately test the chain.
                if (!ConfigManager.Current.DonationAcceptTestEvents && ProbeBool(data, TestProbes))
                {
                    reason = "flagged as a test donation (enable Donations: accept test events to allow)";
                    return true;
                }

                var amount = ResolveAmount(eventType, data);
                string donor = ProbeString(data, DonorProbes);
                string txId = ProbeString(data, IsRecurringShape(eventType) ? RecurringTxIdProbes : TxIdProbes);
                long now = Environment.TickCount64;

                // Plausibility — mirrors the Twitch.Raid viewer-count warning. The
                // amount is broker-supplied and a mis-configured or hostile relay can
                // put anything in it. We warn rather than drop: a real large donation
                // must never be swallowed. Composed here, logged after the dedupe
                // verdict and only for events that PASS: a suppressed replay changes
                // nothing real, and warning on replays would let one replayed
                // transaction with a big number flood this line at broker rate — the
                // same hole the mismatch warning's once-gate closes.
                string? implausible = null;
                if (amount.MinorUnits > ImplausibleMinorUnits)
                {
                    implausible =
                        $"Donation {eventType} reports {amount.ToInvariantString()} {amount.Currency} — above the plausibility threshold. " +
                        "Donation amounts are broker-supplied and spoofable; validate before granting amount-gated rewards.";
                }

                long minorUnits = amount.HasValue ? amount.MinorUnits : UnknownMinorUnits;

                // Composed under the lock, logged after it — a GlobalLogger sink can run
                // arbitrary handlers, and holding the ingest gate across that would let a
                // slow subscriber stall every inbound donation.
                string? amountMismatch = null;
                bool suppress = false;

                lock (_gate)
                {
                    // LAYER 1 — exact replay of one broker's own event. Identity is the
                    // transaction id ALONE; the amount is evidence, not identity.
                    if (txId.Length > 0)
                    {
                        string exactKey = eventType + "|" + txId;
                        if (_seenExact.TryGetValue(exactKey, out SeenEntry prior))
                        {
                            if (!prior.MismatchWarned
                                && prior.MinorUnits != UnknownMinorUnits
                                && minorUnits != UnknownMinorUnits
                                && prior.MinorUnits != minorUnits)
                            {
                                // The real tamper signal: same transaction, different money.
                                // Reported, never acted on — it must not be able to mint a
                                // second payout, so the drop happens either way. Warned ONCE
                                // per (event type, transaction): the payload is attacker-
                                // influenced, and an ungated line here let a hostile or
                                // mis-configured relay replaying one transaction flood the
                                // log at broker rate. The flag lives on the seen-entry, so
                                // the gate's lifetime is exactly the dedupe entry's own.
                                _seenExact[exactKey] = prior with { MismatchWarned = true };
                                amountMismatch =
                                    $"Donation {eventType} replayed transaction {txId} with a DIFFERENT amount " +
                                    $"({prior.MinorUnits} → {minorUnits} minor units). The replay was dropped as a " +
                                    "duplicate. Amounts are broker-supplied; a change under a known transaction id " +
                                    "means the relay is mis-configured or the payload was altered " +
                                    "(one-time notice per transaction).";
                            }

                            reason = $"duplicate of an already-processed transaction ({txId})";
                            suppress = true;
                        }
                        else
                        {
                            Remember(_seenExact, exactKey, now, minorUnits);
                        }
                    }

                    // LAYER 2 — the same donation relayed by a second broker. Skipped for
                    // anonymous donors, where the donor half of the key identifies no one
                    // and would collapse two unrelated tips into one.
                    if (!suppress && amount.HasValue && !IsAnonymousDonor(donor))
                    {
                        string fuzzyKey = donor.Trim().ToLowerInvariant() + "|" +
                                          amount.MinorUnits.ToString(CultureInfo.InvariantCulture) + "|" +
                                          amount.Currency;
                        if (_seenFuzzy.TryGetValue(fuzzyKey, out SeenEntry priorFuzzy) &&
                            now - priorFuzzy.AtMs < CrossBrokerWindowMs)
                        {
                            reason = "the same donor+amount already arrived from another broker within the relay window";
                            suppress = true;
                        }
                        else
                        {
                            Remember(_seenFuzzy, fuzzyKey, now, minorUnits);
                        }
                    }
                }

                if (amountMismatch is not null)
                    GlobalLogger.Log(amountMismatch, "DonationIngest", LogLevel.Communication);
                if (!suppress && implausible is not null)
                    GlobalLogger.Log(implausible, "DonationIngest", LogLevel.Communication);

                return suppress;
            }
            catch (Exception ex)
            {
                // Best-effort policy: on any fault let the event through rather than
                // silently swallowing a real donation.
                GlobalLogger.Error("DonationIngest", $"ingest policy failed for {eventType} — event allowed through", ex);
                return false;
            }
        }

        // A single donation above this is almost certainly a mis-configured relay or
        // a spoof rather than a real tip. 100,000 in minor units = 1,000.00 of a
        // 2-decimal currency. Warn-only.
        private const long ImplausibleMinorUnits = 100_000;

        /// <summary>Bounded insert — sweep expired, hard-cap, then add.</summary>
        private static void Remember(Dictionary<string, SeenEntry> map, string key, long now, long minorUnits)
        {
            if (map.Count >= DedupeMaxKeys)
            {
                var stale = new List<string>();
                foreach (var kv in map)
                    if (now - kv.Value.AtMs > CrossBrokerWindowMs) stale.Add(kv.Key);
                foreach (var k in stale) map.Remove(k);

                // Still full after the sweep (a burst of unique donations): drop the
                // oldest half. An unbounded map on an event-driven path is a leak.
                if (map.Count >= DedupeMaxKeys)
                {
                    var byAge = new List<KeyValuePair<string, SeenEntry>>(map);
                    byAge.Sort((a, b) => a.Value.AtMs.CompareTo(b.Value.AtMs));
                    for (int i = 0; i < byAge.Count / 2; i++) map.Remove(byAge[i].Key);
                }
            }
            map[key] = new SeenEntry(now, minorUnits);
        }

        /// <summary>Test seam — clears both de-dupe maps.</summary>
        internal static void ResetForTests()
        {
            lock (_gate) { _seenExact.Clear(); _seenFuzzy.Clear(); }
        }

        // ── Canonical money ──────────────────────────────────────────────────

        /// <summary>
        /// Writes the canonical money vars for a donation event, overwriting whatever
        /// raw shape the catalog resolver produced. Runs inside
        /// <c>BuildGenericEventVars</c>, so it must stay pure and allocation-light.
        /// </summary>
        internal static void NormalizeMoneyVars(string eventType, JsonElement data, IDictionary<string, string> vars)
        {
            try
            {
                if (!IsMoneyEvent(eventType)) return;

                var amount = ResolveAmount(eventType, data, out MoneyScale scale, out int exponent);
                if (!amount.HasValue)
                {
                    NoteProbeMissOnce(eventType, data);
                    return;
                }

                // ── The amount is ALWAYS canonicalised ───────────────────────
                //
                // This is C1's shipped contract and three consumers depend on it: the {amount}
                // alert token, TimerService's subathon seconds and LoyaltyService's tip points all
                // read event.amount as MAJOR units. The amount's own scale is never in doubt by the
                // time we get here — ResolveAmount only returns a value once the payload has stated
                // it, via an explicit minor-unit field, its own decimalPlaces exponent, or a decimal
                // literal. Withholding this write does not leave a neutral "raw" state: the catalog
                // resolver already wrote the RAW minor-unit integer into event.amount
                // (PlatformEventCatalog's Str("Amount", "charity.currentAmount.value", …)), so
                // skipping the overwrite ships the exact 100x figure this class exists to remove.
                vars["event.amount"] = amount.ToInvariantString();
                vars["event.amount_cents"] = amount.MinorUnits.ToString(CultureInfo.InvariantCulture);

                // ── The campaign TARGET — canonicalised only when its scale is stated ─────
                //
                // A charity campaign event carries a TARGET beside the running total, and the
                // catalog writes it RAW. Canonicalising the amount alone does not fix the 100x
                // charity defect so much as RELOCATE it: a canonical `26.00` against a raw
                // `1000000` is the same 100x error, now sitting between two vars of one event.
                //
                // So the target is rewritten only when the payload states its scale — never by
                // borrowing a verdict from the figure beside it. When it does not, the target is
                // LEFT ALONE (raw, exactly as today) and the refusal is logged once. That is
                // strictly better than the alternative of withholding both: the amount is correct
                // either way, and the untouched target is no worse than the status quo this sprint
                // inherited. The residual hazard — a script mixing a canonical amount with a raw
                // target — is what the node documentation is corrected to warn about.
                //
                // ★ CONSTRAINT THAT SHAPES THE GATE. Channel goals (Twitch.GoalBegin /
                // GoalProgress / GoalEnd) also carry a TargetAmount, but theirs is a COUNT —
                // 200 followers, 50 subs — and dividing it by a currency exponent would report a
                // 200-follower goal as 2.00. Two gates keep that impossible: this method returns
                // above unless IsMoneyEvent, which the Goal* family is not, and CampaignTargetEvents
                // below names the three charity CAMPAIGN events explicitly rather than testing for
                // "an event that happens to have a target".
                //
                // The set test comes first so a broker tip — the overwhelmingly common money
                // event, and one that has no target at all — pays nothing for this block beyond
                // one hash lookup.
                if (CampaignTargetEvents.Contains(eventType))
                {
                    string? targetVar = TargetVarFor(eventType);
                    if (targetVar is not null && ProbeString(data, TargetProbes).Length > 0)
                    {
                        if (TryResolveCampaignTarget(data, amount, scale, exponent,
                                                     out var target, out string reasonCode, out string reason)
                            && target.HasValue)
                            vars[targetVar] = target.ToInvariantString();
                        else
                            NoteTargetRefusalOnce(eventType, reasonCode, reason, data);
                    }
                }
                if (amount.Currency.Length > 0) vars["event.currency"] = amount.Currency;

                int dot = eventType.IndexOf('.');
                if (dot > 0) vars["event.source"] = eventType.Substring(0, dot);

                vars["event.is_test"] = ProbeBool(data, TestProbes) ? "true" : "false";

                string txId = ProbeString(data, TxIdProbes);
                if (txId.Length > 0) vars["event.tx_id"] = txId;

                string donor = ProbeString(data, DonorProbes);
                if (donor.Length > 0 && !vars.ContainsKey("user.name")) vars["user.name"] = donor;
            }
            catch { /* best effort — never break var extraction */ }
        }

        /// <summary>
        /// How a payload stated the SCALE of a money figure. The verdict is an output of
        /// <see cref="ResolveAmount(string, JsonElement, out MoneyScale, out int)"/> because a
        /// campaign target must be read under the amount's own verdict — reading it under a
        /// looser one is what puts an amount and its target 100x apart.
        /// </summary>
        internal enum MoneyScale
        {
            /// <summary>Nothing readable.</summary>
            None = 0,
            /// <summary>An explicit integer minor-unit field (<c>valueMinor</c>, <c>*InCents</c>).</summary>
            MinorUnitField,
            /// <summary>An integer <c>value</c> beside a <c>decimalPlaces</c> exponent.</summary>
            Exponent,
            /// <summary>A free-form figure already written in MAJOR units.</summary>
            MajorLiteral,
        }

        /// <summary>
        /// Resolves an amount from any broker shape.
        ///
        /// Twitch charity is the one payload documented to be an integer + exponent
        /// pair rather than a decimal, so it is read exactly rather than parsed.
        /// </summary>
        internal static DonationAmount ResolveAmount(string eventType, JsonElement data)
            => ResolveAmount(eventType, data, out _, out _);

        /// <summary>
        /// The same resolution, reporting HOW the scale was established. <paramref name="exponent"/>
        /// is meaningful only for <see cref="MoneyScale.Exponent"/>, where it is the payload's own
        /// <c>decimalPlaces</c> — the divisor a paired target must reuse.
        /// </summary>
        internal static DonationAmount ResolveAmount(
            string eventType, JsonElement data, out MoneyScale scale, out int exponent)
        {
            scale = MoneyScale.None;
            exponent = 0;
            if (data.ValueKind != JsonValueKind.Object) return DonationAmount.None;

            string currency = ProbeString(data, CurrencyProbes);

            // ── Exact forms first, in decreasing order of trustworthiness ────
            //
            // (1) An explicit integer MINOR-UNIT field. Streamer.bot normalises
            //     Twitch charity into `value` (decimal) + `valueMinor` (integer),
            //     and Pally.gg / Patreon report cents directly. Reading the
            //     integer avoids the decimal round-trip entirely.
            if (TryGetLong(data, out long minor, MinorUnitProbes))
            {
                string cur = currency.Length > 0 ? currency : ProbeString(data, CurrencyProbes);
                int exp = DonationAmount.ExponentFor(cur);
                scale = MoneyScale.MinorUnitField;
                exponent = exp;
                return DonationAmount.FromDecimal(minor / Pow10(exp), cur);
            }

            // (2) Twitch's RAW EventSub shape — integer `value` plus a separate
            //     `decimalPlaces` exponent, never a float. Streamer.bot's own docs
            //     describe the normalised form instead, but the WebSocket payload
            //     is undocumented and may pass the raw pair straight through, so
            //     both are handled.
            if (TryReadValueWithExponent(data, currency, out var exact, out int rootPlaces))
            {
                scale = MoneyScale.Exponent;
                exponent = rootPlaces;
                return exact;
            }

            // (3) A nested amount object ({ amount: { value, currency } }).
            //
            // ★ The probe list is DOTTED. Step (2) reads the exponent pair off the
            // element it is handed, and its own probe names carry no dots, so on
            // Twitch's raw charity shape — { charity: { amount: { value,
            // decimalPlaces } } } — it found nothing at the root. The bare
            // "amount" lookup this step used to do missed for the same reason (the
            // root property is "charity"), and control fell through to step (4),
            // whose AmountProbes ARE dotted: it resolved charity.amount.value and
            // parsed the bare MINOR-UNIT integer as a major amount. A $5.00 charity
            // gift ingested as $500.00 and drove alert text, Loyalty points and
            // subathon seconds at 100x. Resolving the amount OBJECT at its real
            // depth first is what closes that; the flat "amount" case is preserved
            // as the last entry.
            foreach (var objProbe in AmountObjectProbes)
            {
                if (!TryResolve(data, objProbe, out var amtEl) || amtEl.ValueKind != JsonValueKind.Object) continue;
                string nestedCur = ProbeString(amtEl, CurrencyProbes);
                if (nestedCur.Length > 0) currency = nestedCur;
                if (TryReadValueWithExponent(amtEl, currency, out var nestedExact, out int nestedPlaces))
                {
                    scale = MoneyScale.Exponent;
                    exponent = nestedPlaces;
                    return nestedExact;
                }
                string nestedRaw = ProbeString(amtEl, AmountProbes);
                if (nestedRaw.Length > 0)
                {
                    scale = MoneyScale.MajorLiteral;
                    return DonationAmount.Parse(nestedRaw, currency);
                }
            }

            // (4) Free-form string / number in any broker shape.
            string raw = ProbeString(data, AmountProbes);
            scale = MoneyScale.MajorLiteral;
            return DonationAmount.Parse(raw, currency);
        }

        // ── The charity CAMPAIGN target ──────────────────────────────────────
        //
        // The three campaign events are the ONLY money events whose catalog entry declares a
        // TargetAmount socket; the ten brokers declare none and Twitch.CharityDonation is a
        // single gift with no target at all (the same split GoalChannelProducer.CharityEvents
        // draws, for the same reason). Named explicitly rather than derived from "does this
        // event have a target socket" so the Twitch.Goal* COUNT targets can never join by
        // accident — see the constraint note in NormalizeMoneyVars.
        private static readonly HashSet<string> CampaignTargetEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            "Twitch.CharityStarted", "Twitch.CharityProgress", "Twitch.CharityCompleted",
        };

        // EXACTLY the catalog's own TargetAmount probes (PlatformEventCatalog's
        // TwitchCharityProgress factory). Identical on purpose: "a target the catalog wrote into
        // event.targetamount" and "a target this file can normalise" must be the same question,
        // or a broader list here would rewrite a var the catalog never produced.
        private static readonly string[] TargetProbes = { "charity.targetAmount.value", "targetAmount" };

        // Paths at which a target OBJECT ({ value, decimalPlaces, currency }) can sit, most
        // specific first — the mirror of AmountObjectProbes, and for the same reason: the exponent
        // reader probes UNDOTTED names against whatever element it is handed, so a target nested
        // under charity.* is only reachable by handing it the nested object.
        private static readonly string[] TargetObjectProbes =
        {
            "charity.targetAmount", "targetAmount",
        };

        // The target's integer minor-unit twin, dotted so it resolves at its real depth (both the
        // nested WebSocket shape and the flattened trigger-variable key).
        private static readonly string[] TargetMinorUnitProbes =
        {
            "charity.targetAmount.valueMinor", "targetAmount.valueMinor",
        };

        /// <summary>
        /// The var the event's TargetAmount socket binds to, or null when it declares none.
        ///
        /// <para>Read out of the catalog rather than spelled as a literal: the catalog already IS
        /// the socket→token contract (ScriptExporter resolves the same pin through
        /// <c>PlatformEventSocket.VarToken</c>), and a third hand-written copy of
        /// <c>event.targetamount</c> is a rename away from writing a key no pin reads.</para>
        /// </summary>
        private static string? TargetVarFor(string eventType)
        {
            // FindForEvent: the argument is a raw wire event type, not a node title.
            var def = PlatformEventCatalog.FindForEvent(eventType);
            if (def is null) return null;
            foreach (var socket in def.Sockets)
                if (string.Equals(socket.Name, "TargetAmount", StringComparison.Ordinal))
                    return socket.VarToken;
            return null;
        }

        /// <summary>
        /// Resolves the campaign target under the AMOUNT's scale verdict — never under one of its
        /// own. Returns false, with a distinct <paramref name="reasonCode"/>, whenever the payload
        /// cannot answer on that same scale; the caller then leaves both figures raw.
        ///
        /// <para>The three verdicts mirror the ones
        /// <c>GoalChannelProducer.TryResolveCharityAmounts</c> already applies to the same payload
        /// for the overlay's <c>goal.charity.*</c> bar, so the two consumers of one Twitch event
        /// cannot disagree about what it says:</para>
        ///
        /// <list type="number">
        /// <item><b>Minor-unit field.</b> The amount named its own scale with an integer
        /// <c>valueMinor</c> / <c>*InCents</c>, so the target must carry one too. A
        /// <c>valueMinor</c> numerator over a bare-integer denominator of unknown scale IS the
        /// 100x error, which is why a one-sided <c>valueMinor</c> is refused rather than
        /// guessed.</item>
        /// <item><b>Exponent.</b> ONE exponent — the amount's — divides BOTH sides, even if the
        /// payload repeats it per slot: a campaign has one currency, and a single divisor is what
        /// keeps the ratio right. The target must be an INTEGER for that to mean anything;
        /// <see cref="TryGetLong"/> rejects a fractional JSON number, so an already-scaled decimal
        /// beside a raw exponent refuses instead of being divided a second time.</item>
        /// <item><b>Major literal.</b> The amount was read as a plain major figure with no
        /// minor-unit signal anywhere, so the target is parsed the same way through
        /// <see cref="DonationAmount.Parse"/> — the same decorated / European / grouped shapes,
        /// the same result scale. Nothing is being scaled here, so there is nothing to get
        /// 100x wrong; both sides simply keep the units the payload used.</item>
        /// </list>
        /// </summary>
        private static bool TryResolveCampaignTarget(
            JsonElement data, DonationAmount amount, MoneyScale scale, int exponent,
            out DonationAmount target, out string reasonCode, out string reason)
        {
            target = DonationAmount.None;
            reasonCode = "";
            reason = "";

            // The campaign's currency comes off the amount, which already probed the payload and
            // upper-cased the ISO code. A per-slot currency would be a second verdict.
            string currency = amount.Currency;

            // (1) The target as an OBJECT carrying its OWN decimalPlaces — Twitch's documented
            //     EventSub shape, { target_amount: { value, decimal_places, currency } }. This is
            //     the case that matters most and the one that needs no borrowed verdict at all:
            //     the payload states the target's scale itself, so the amount beside it is
            //     irrelevant. Resolved first for exactly that reason.
            foreach (var objProbe in TargetObjectProbes)
            {
                if (!TryResolve(data, objProbe, out var tEl) || tEl.ValueKind != JsonValueKind.Object) continue;
                if (TryReadValueWithExponent(tEl, currency, out var stated, out _) && stated.HasValue)
                {
                    target = stated;
                    return true;
                }
            }

            // (2) An explicit integer minor-unit twin — same trustworthiness, flat shape.
            if (TryGetLong(data, out long minorTarget, TargetMinorUnitProbes))
            {
                target = DonationAmount.FromDecimal(
                    minorTarget / Pow10(DonationAmount.ExponentFor(currency)), currency);
                if (target.HasValue) return true;
            }

            // (3) A scalar target. A written decimal separator settles it — nobody expresses minor
            //     units with a fractional part, so "500.00" beside a minor-scaled amount is a
            //     MAJOR figure, not a candidate for division. That distinction is what keeps the
            //     commonest real payload out of the refusal path.
            string raw = ProbeString(data, TargetProbes);
            bool statesItsOwnScale = raw.IndexOf('.') >= 0 || raw.IndexOf(',') >= 0;
            bool amountWasMinorScaled = scale is MoneyScale.Exponent or MoneyScale.MinorUnitField;

            // (4) A BARE INTEGER beside a minor-scaled amount is genuinely ambiguous — 1000000
            //     could be $10,000.00 in cents or a $1,000,000 goal in whole currency, and the
            //     payload says nothing either way. Guessing here is how the 100x defect is
            //     relocated rather than fixed, so this refuses and the target stays raw.
            if (amountWasMinorScaled && !statesItsOwnScale)
            {
                reasonCode = "charity-target-scale-unstated";
                reason = "reported its running total on a minor-unit scale but gave the campaign target "
                       + "as a bare integer with no exponent, minor-unit field or decimal separator of "
                       + "its own (probes: charity.targetAmount / targetAmount), so whether it means "
                       + "minor or whole currency units is unstated. The target is left exactly as sent";
                return false;
            }

            target = DonationAmount.Parse(raw, currency);
            if (!target.HasValue)
            {
                target = DonationAmount.None;
                reasonCode = "charity-target-unreadable";
                reason = "carried a campaign target that resolved to no usable figure (probes: "
                       + "charity.targetAmount.value / targetAmount)";
                return false;
            }
            return true;
        }

        /// <summary>10^<paramref name="exp"/> as a decimal — the minor-unit divisor.</summary>
        private static decimal Pow10(int exp)
        {
            decimal scale = 1m;
            for (int i = 0; i < exp; i++) scale *= 10m;
            return scale;
        }

        // Integer minor-unit fields, most specific first. Kept separate from
        // AmountProbes because hitting one of these means NO decimal parsing is
        // needed — the exact value is already in the payload.
        private static readonly string[] MinorUnitProbes =
        {
            "charity.amount.valueMinor", "charity.currentAmount.valueMinor",
            "tipNetAmountInCents",
            "attributes.currently_entitled_amount_cents",
            "amountInCents", "valueMinor",
        };

        // Paths at which an amount OBJECT ({ value, decimalPlaces, currency }) can sit,
        // most specific first. These must be DOTTED and must be resolved before the
        // free-form scalar sweep: the exponent reader probes undotted names against
        // whatever element it is given, so an amount nested under charity.* is only
        // reachable by handing it the nested object. The bare "amount" entry is last so
        // the historical flat shape keeps its exact previous behaviour.
        private static readonly string[] AmountObjectProbes =
        {
            "charity.amount", "charity.currentAmount", "amount",
        };

        /// <summary>
        /// Reads an integer value + decimal-places exponent pair. Returns false
        /// unless BOTH are present — a bare integer `value` with no exponent is a
        /// whole-currency amount and belongs on the ordinary parse path.
        ///
        /// <para><paramref name="places"/> is reported out because a campaign target must be
        /// divided by the SAME exponent as the amount beside it.</para>
        /// </summary>
        private static bool TryReadValueWithExponent(
            JsonElement el, string currency, out DonationAmount amount, out int places)
        {
            amount = DonationAmount.None;
            places = 0;
            if (el.ValueKind != JsonValueKind.Object) return false;

            if (!TryGetInt(el, out places, "decimalPlaces", "decimal_places")) return false;
            if (!TryGetLong(el, out long value, "value", "amount")) return false;

            string cur = currency;
            if (cur.Length == 0) cur = ProbeString(el, CurrencyProbes);
            amount = DonationAmount.FromMinorUnits(value, places, cur);
            return true;
        }

        private static bool TryGetLong(JsonElement el, out long value, params string[] keys)
        {
            value = 0;
            foreach (var k in keys)
            {
                if (!TryResolve(el, k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value)) return true;
                if (v.ValueKind == JsonValueKind.String &&
                    long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            }
            return false;
        }

        private static bool TryGetInt(JsonElement el, out int value, params string[] keys)
        {
            value = 0;
            foreach (var k in keys)
            {
                if (!TryResolve(el, k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value)) return true;
                if (v.ValueKind == JsonValueKind.String &&
                    int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves one candidate key three ways, in order: a nested walk down a
        /// dotted path, the whole dotted string as one flat key, then the plain
        /// key. Mirrors <c>PlatformEventVarResolver.TryResolveElement</c> — SB's
        /// trigger-variable exports flatten nested groups into dotted key names,
        /// so both shapes must hit.
        /// </summary>
        private static bool TryResolve(JsonElement data, string probe, out JsonElement found)
        {
            found = default;
            if (data.ValueKind != JsonValueKind.Object) return false;

            if (probe.IndexOf('.') >= 0)
            {
                // Nested walk.
                var cur = data;
                bool ok = true;
                foreach (var seg in probe.Split('.'))
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) { ok = false; break; }
                }
                if (ok) { found = cur; return true; }

                // Flat dotted key.
                if (data.TryGetProperty(probe, out found)) return true;
                return false;
            }

            return data.TryGetProperty(probe, out found);
        }

        private static string ProbeString(JsonElement el, string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return "";
            foreach (var k in keys)
            {
                if (!TryResolve(el, k, out var v)) continue;
                switch (v.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s!;
                        break;
                    case JsonValueKind.Number:
                        return v.GetRawText();
                    case JsonValueKind.Object:
                        // Nested actor object ({ user: { name } }) — take its name.
                        // Fixed candidate list, NOT DonorProbes: recursing with the
                        // full donor list would re-enter dotted paths against a
                        // sub-object and could match something unrelated.
                        foreach (var nk in new[] { "name", "login", "displayName", "userName", "full_name" })
                            if (v.TryGetProperty(nk, out var nv) && nv.ValueKind == JsonValueKind.String)
                            {
                                var ns = nv.GetString();
                                if (!string.IsNullOrWhiteSpace(ns)) return ns!;
                            }
                        break;
                }
            }
            return "";
        }

        private static bool ProbeBool(JsonElement el, string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            foreach (var k in keys)
            {
                if (!TryResolve(el, k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.String)
                    return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // ── Probe-drift diagnostic ───────────────────────────────────────────
        // Every field name above is a best-effort guess against an unpublished
        // schema. When a donation arrives and NO amount could be read, print the
        // payload's ACTUAL field names once per event type, so a wrong probe is a
        // one-line fix from a single live capture instead of a silent zero.
        //
        // ★ Keyed by (event type, REASON CODE), not by event type alone: an unreadable amount and
        // a refused campaign target are different defects with different one-line fixes, and a
        // shared key would let whichever an event type hits FIRST permanently mask the other.
        // Same keying, for the same reason, as GoalChannelProducer.NoteOnce.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ProbeMissWarned = new();
        private const int MaxReportedFields = 24;

        private static void NoteProbeMissOnce(string eventType, JsonElement data)
        {
            if (!ProbeMissWarned.TryAdd(eventType + "|amount-unreadable", 0)) return;
            try
            {
                GlobalLogger.Log(
                    $"Donation {eventType} carried no readable amount. Payload fields: [{DescribeFields(data)}] — " +
                    "the amount probe list needs one of these names (one-time notice per event type).",
                    "DonationIngest", LogLevel.Communication);
            }
            catch { /* diagnostic only */ }
        }

        /// <summary>
        /// Reports a campaign pair whose two sides could not be read on one scale. States the
        /// CONSEQUENCE explicitly, because "nothing was rewritten" is the non-obvious half: the
        /// script vars still resolve, they simply carry Streamer.bot's raw shape.
        /// </summary>
        private static void NoteTargetRefusalOnce(string eventType, string reasonCode, string what, JsonElement data)
        {
            if (!ProbeMissWarned.TryAdd(eventType + "|" + reasonCode, 0)) return;
            try
            {
                GlobalLogger.Log(
                    $"Donation {eventType} {what}. Neither the amount nor the target was rewritten — both keep " +
                    "the raw payload shape so the two stay on ONE scale, which is what a script comparing them " +
                    $"needs. Payload fields: [{DescribeFields(data)}] (one-time notice per event type and reason).",
                    "DonationIngest", LogLevel.Communication);
            }
            catch { /* diagnostic only */ }
        }

        /// <summary>The payload's real top-level field names — what turns a wrong probe into a one-line fix.</summary>
        private static string DescribeFields(JsonElement data)
        {
            var names = new List<string>();
            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in data.EnumerateObject())
                {
                    names.Add(p.Name);
                    if (names.Count >= MaxReportedFields) break;
                }
            }
            return string.Join(", ", names);
        }
    }
}
