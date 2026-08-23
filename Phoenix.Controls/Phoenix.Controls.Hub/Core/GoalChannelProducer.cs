using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// THE producer for the Overlay Live Channel's reserved <c>goal.*</c> root — the half that
    /// was missing when V10 shipped the reader.
    ///
    /// <para>WHY IT EXISTS. <c>Goal.Progress</c> (Visualist.Engine) and <c>evalGoalProgress</c>
    /// (compositor.js) have read <c>goal.&lt;kind&gt;.{current,target,progress,label}</c> since
    /// V10, and C20 landed the <c>Twitch.Goal*</c> / <c>Twitch.Charity*</c> event roots with
    /// their var maps — but nothing joined the two, so a correctly authored goal widget rendered
    /// nothing. TODO.md's V10 line assigns the join here: <i>one goal model, no second data
    /// path</i>. That is why this class publishes into the READER'S keys rather than inventing a
    /// <c>channel.goal.*</c> root of its own.</para>
    ///
    /// <para>Reads the RAW Streamer.bot payload, not the resolved var dictionary, and that is
    /// load-bearing rather than incidental — see <see cref="TryResolveCharityAmounts"/>. It is
    /// also why the hook sits in <c>ScriptManager.ExecuteGenericEventAsync</c>'s pre-guard
    /// region, beside the Timer / Loyalty / Alerts feeds, where the <c>JsonElement</c> is still
    /// in hand.</para>
    ///
    /// <para>SET, NEVER ACCUMULATE. One real charity donation raises BOTH
    /// <c>Twitch.CharityDonation</c> and <c>Twitch.CharityProgress</c>, and charity is
    /// deliberately exempt from <c>DonationIngest</c>'s de-dupe (it is not a third-party broker,
    /// so it needs the money path but not the cross-broker collapse). A producer that added to a
    /// running figure would therefore double-count every gift. Every write here is an absolute
    /// value read out of the payload, so a replay is idempotent by construction.</para>
    /// </summary>
    internal static class GoalChannelProducer
    {
        // ── The key contract ─────────────────────────────────────────────────
        //
        // Hard-coded rather than referenced, for the same reason CountersService hard-codes
        // "counter." and TimerService hard-codes "timer.": Hub references only Shared, and the
        // reader-side constants (NodeTemplates.GoalKeyPrefix / GoalFields / GoalKinds) live in
        // Visualist.Engine. The pairing is pinned from the test project, which references both —
        // GoalChannelProducerTests asserts these literals against those constants, exactly as
        // WidgetFamilyV10Tests pins compositor.js's mirrored literals. That test is not ceremony:
        // a producer and a reader that spell one field differently produce a permanently blank
        // widget with a running producer, a valid graph and no error on either side.
        internal const string KeyPrefix = "goal.";

        internal const string FieldCurrent  = "current";
        internal const string FieldTarget   = "target";
        internal const string FieldProgress = "progress";
        internal const string FieldLabel    = "label";

        internal const string KindFollower = "follower";
        internal const string KindSub      = "sub";
        internal const string KindBits     = "bits";
        internal const string KindCharity  = "charity";

        /// <summary>
        /// Prefix a goal type outside the reserved vocabulary carries. Mirrors
        /// <c>NodeTemplates.GoalCustomKindPrefix</c>.
        /// </summary>
        internal const string CustomKindPrefix = "custom_";

        /// <summary>
        /// Provenance tag. ONE spelling for all six events on purpose: the store scopes
        /// <c>ExpectedInterval</c> inheritance by Source string equality, so a second tag would
        /// fragment provenance for no gain (the same call CountersService records).
        /// </summary>
        internal const string LiveSource = "platform:TwitchGoals";

        // No ExpectedInterval anywhere in this file. Goal and charity updates are event-driven —
        // Twitch pushes a channel.goal.progress when the count moves and never on a cadence — so
        // declaring one would decay a perfectly healthy goal to Stale within seconds of the last
        // contribution. compositor.js's liveGoalState already documents that this family answers
        // Active or Missing in practice; this is the publisher-side half of that statement.

        // ── Which events feed the family ─────────────────────────────────────

        /// <summary>
        /// The three <c>Twitch.Goal*</c> events. Their amounts are COUNTS (follower count, sub
        /// count, bit count) — <c>PlatformEventCatalog</c> declares both as <c>Int</c> — so they
        /// never touch <c>DonationAmount</c>, currency probes or any other money logic.
        /// <c>Twitch.GoalEnd</c> is included so the bar settles on the goal's final figures
        /// instead of freezing on the last progress tick.
        /// </summary>
        private static readonly HashSet<string> GoalEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            "Twitch.GoalBegin", "Twitch.GoalProgress", "Twitch.GoalEnd",
        };

        /// <summary>
        /// The three charity CAMPAIGN events — the ones that carry a running total AND a target.
        ///
        /// <para>★ <c>Twitch.CharityDonation</c> is deliberately ABSENT. It is one donor's single
        /// gift with no target at all (a different catalog shape: <c>charity.amount.*</c> plus a
        /// <c>User</c>), which makes it a TIP source, not a goal source. Wiring it in would write
        /// one person's gift into <c>goal.charity.current</c> and overwrite the campaign total
        /// with it — and because the same donation also raises <c>Twitch.CharityProgress</c>, the
        /// two would fight over one key on every gift.</para>
        /// </summary>
        private static readonly HashSet<string> CharityEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            "Twitch.CharityStarted", "Twitch.CharityProgress", "Twitch.CharityCompleted",
        };

        /// <summary>True when <paramref name="eventType"/> feeds a <c>goal.*</c> root.</summary>
        internal static bool Handles(string? eventType)
            => eventType is not null && (GoalEvents.Contains(eventType) || CharityEvents.Contains(eventType));

        // ── Probe candidates ─────────────────────────────────────────────────
        //
        // Mirrors the catalog's own probe lists (PlatformEventCatalog's TwitchGoal /
        // TwitchCharityProgress factories) so the producer and the script vars read the same
        // fields — with the flat/nested three-way resolution both surfaces need, because
        // Streamer.bot's WebSocket payload nests while its trigger-variable export flattens the
        // same data into dotted key names.

        // snake_case entries: the live ≤1.0.4 wire spells the goal amounts
        // current_amount/target_amount (captured 2026-08-23 — the producer's own
        // WarnOnce printed the real field names after five GoalProgress events
        // updated nothing), matching the snake_case charity shape DonationIngest
        // already documents. camelCase kept first for the trigger-variable
        // surface and newer wires.
        private static readonly string[] GoalTypeProbes    = { "goal.type",          "type" };
        private static readonly string[] GoalLabelProbes   = { "goal.description",   "description" };
        private static readonly string[] GoalCurrentProbes = { "goal.currentAmount", "currentAmount", "current_amount" };
        private static readonly string[] GoalTargetProbes  = { "goal.targetAmount",  "targetAmount",  "target_amount" };

        private static readonly string[] CharityLabelProbes = { "charity.name", "charityName" };

        // Catalog order: the documented nested path first, the bare key second.
        private static readonly string[] CharityCurrentProbes =
            { "charity.currentAmount.value", "charity.amount.value", "amount" };
        private static readonly string[] CharityTargetProbes =
            { "charity.targetAmount.value", "targetAmount" };
        private static readonly string[] CharityCurrencyProbes =
            { "charity.currentAmount.currency", "charity.amount.currency",
              "charity.targetAmount.currency", "currency" };

        // The minor-unit exponent. Probed on the CURRENT element first (it is the numerator's own
        // scale), then the target's, then the single-gift element, then the payload root.
        //
        // ★ Both dotted and root forms are probed here ON PURPOSE, and the reason is a defect
        // recorded in contract §4h against DonationIngest.TryReadValueWithExponent: that helper
        // probes `decimalPlaces` / `decimal_places` UNDOTTED against whatever element it was
        // handed, and its TryResolve only nested-walks a probe CONTAINING a dot — so for charity
        // the exponent is looked for at the payload root while the data sits one level deeper at
        // charity.<slot>.*. Its raw-exponent path can therefore never fire for the very shape its
        // comment names. That defect belongs to Box C (DonationIngest is not this box's file);
        // this producer must not inherit it, hence the dotted probes.
        private static readonly string[] CharityExponentProbes =
        {
            "charity.currentAmount.decimal_places", "charity.currentAmount.decimalPlaces",
            "charity.targetAmount.decimal_places",  "charity.targetAmount.decimalPlaces",
            "charity.amount.decimal_places",        "charity.amount.decimalPlaces",
            "decimal_places",                       "decimalPlaces",
        };

        // Explicit integer minor-unit fields — the shape DonationIngest's own primary path
        // expects Streamer.bot to normalise charity into (`value` decimal + `valueMinor`
        // integer). Accepted as a SECOND scale verdict, and only as a PAIR: see
        // TryResolveCharityAmounts for why one side alone is refused.
        private static readonly string[] CharityCurrentMinorProbes =
            { "charity.currentAmount.valueMinor", "charity.amount.valueMinor" };
        private static readonly string[] CharityTargetMinorProbes =
            { "charity.targetAmount.valueMinor" };

        // ── Entry points ─────────────────────────────────────────────────────

        /// <summary>
        /// Production entry point — publishes into the process-wide channel.
        /// Never throws: a producer fault must not break event dispatch.
        /// </summary>
        internal static void Publish(string eventType, JsonElement data)
            => PublishTo(OverlayLiveStore.Instance, eventType, data);

        /// <summary>
        /// Publishes one platform event's goal fields into <paramref name="store"/>.
        ///
        /// <para>Split from <see cref="Publish"/> so the test suite binds its OWN store — sharing
        /// the production singleton across tests is exactly the cross-test coupling the store's
        /// internal ctor exists to avoid.</para>
        /// </summary>
        internal static void PublishTo(OverlayLiveStore store, string eventType, JsonElement data)
        {
            if (store is null || string.IsNullOrEmpty(eventType)) return;
            try
            {
                if (CharityEvents.Contains(eventType)) { PublishCharity(store, eventType, data); return; }
                if (GoalEvents.Contains(eventType))    { PublishCountGoal(store, eventType, data); }
            }
            catch (Exception ex)
            {
                // Belt and braces behind the caller's AsyncErrorBoundary: a malformed payload must
                // cost the overlay one stale bar, never the event dispatch that feeds Timer,
                // Loyalty, Alerts and every .phx script downstream of it.
                GlobalLogger.Error("GoalChannelProducer", $"goal.* publish failed for {eventType}", ex);
            }
        }

        // ── The count goals: follower / sub / bits / custom_<type> ───────────

        /// <summary>
        /// Publishes a <c>Twitch.Goal*</c> event. NO currency handling anywhere on this path:
        /// <c>CurrentAmount</c> / <c>TargetAmount</c> are integer counts, and routing a follower
        /// count through <see cref="DonationAmount"/> would divide it by a currency exponent and
        /// report 250 followers as 2.50.
        ///
        /// <para><c>label</c> always publishes; <c>current</c> / <c>target</c> publish when readable;
        /// <c>progress</c> publishes only when BOTH counts were readable, because a computed 0 over
        /// an unreadable side would overwrite a correct bar. Anything unreadable emits ONE
        /// diagnostic naming the payload's real fields — this path was silent before.</para>
        /// </summary>
        private static void PublishCountGoal(OverlayLiveStore store, string eventType, JsonElement data)
        {
            string rawType = ProbeString(data, GoalTypeProbes);
            string? kind = KindForGoalType(rawType);
            if (kind is null)
            {
                // No GoalType means no kind, and an empty kind would build the root "goal.." —
                // whose fields ("goal..current") are a key nobody can bind and nothing can read.
                // Refuse rather than publish a key that cannot be consumed.
                NoteOnce(eventType, "no-goal-type", data,
                    "carried no readable goal type (probes: goal.type / type), so no goal.<kind>.* "
                    + "root could be formed and nothing at all was published");
                return;
            }

            string root = KeyPrefix + kind + ".";

            bool haveCurrent = TryProbeNumber(data, GoalCurrentProbes, out decimal current);
            bool haveTarget  = TryProbeNumber(data, GoalTargetProbes,  out decimal target);

            // An absent count is OMITTED, not published as 0. Missing is an honest state the
            // reader renders (State = Missing, the pin reads 0 and the caption renders nothing);
            // a published 0 is a claim that the goal is genuinely at zero.
            if (haveCurrent) PublishNumber(store, root + FieldCurrent, current);
            if (haveTarget)  PublishNumber(store, root + FieldTarget,  target);

            // ★ The ratio is held to the SAME standard as the two pins above, and for a stronger
            // reason. It is not read out of the payload, it is COMPUTED — so publishing it when a
            // side is unreadable publishes an invented 0 over whatever a previous, correct event
            // put there: a 60% bar drops to 0 on one malformed event, liveGoalState still reports
            // the root Active (so the widget renders a CONFIDENT empty bar instead of declining),
            // and goalProgressOf gives a PUBLISHED progress absolute priority over deriving one
            // from current/target — so the invented 0 also closes the reader's own fallback.
            // Omitting it leaves exactly the shape goalProgressOf documents for a producer that
            // fills part of the family: current + target published, progress derived.
            if (haveCurrent && haveTarget)
            {
                PublishNumber(store, root + FieldProgress, Progress01(current, target, true));
            }
            else if (!haveCurrent && !haveTarget)
            {
                // ★ The count path used to be SILENT here. Every probe name in this file is a
                // best-effort guess against a schema Streamer.bot does not publish, and this
                // diagnostic exists to print the payload's ACTUAL field names once so a wrong
                // probe becomes a one-line fix from a single live capture. Without this branch
                // that promise held for 3 of the 6 wired events — and not for the three whose
                // field names are least confirmed.
                NoteOnce(eventType, "count-amounts-missing", data,
                    "carried neither a readable current nor a readable target count (probes: "
                    + "goal.currentAmount / currentAmount, goal.targetAmount / targetAmount), so no "
                    + "progress ratio could be formed; every goal." + kind + ".* number is left as "
                    + "whatever a previous event published rather than overwritten with 0");
            }
            else
            {
                NoteOnce(eventType, "count-amounts-partial", data,
                    "carried only " + (haveCurrent ? "a current" : "a target") + " count and no "
                    + (haveCurrent ? "target" : "current") + " (probes: goal.currentAmount / "
                    + "currentAmount, goal.targetAmount / targetAmount), so no progress ratio could "
                    + "be formed; goal." + kind + ".progress is left as whatever a previous event "
                    + "published rather than overwritten with 0");
            }

            // Present-and-empty beats absent for the label: a Missing label is indistinguishable
            // from a Hub that never spoke, and liveGoalState judges the whole root. Unlike the
            // ratio the label is READ, not computed — an empty one is the accurate reading of a
            // payload that carries no description, not a fabricated figure — so it publishes on
            // every branch above.
            store.PublishString(root + FieldLabel, ProbeString(data, GoalLabelProbes), LiveSource);
        }

        /// <summary>
        /// Folds Twitch's <c>GoalType</c> into the reserved <c>goal.*</c> kind vocabulary, or
        /// into <c>custom_&lt;slug&gt;</c> when it is a type this build has never heard of.
        /// Returns null only when there is no type at all.
        ///
        /// <para>★ THIS IS THE ONE DOCUMENTED PLACE THAT LOWER-CASES A <c>custom_</c> SLUG, and
        /// it is CASE 2 of <c>NodeTemplates.GoalCustomKindPrefix</c>'s two-case rule — the
        /// MACHINE-derived slug. There is no author text to preserve here: the kind is folded out
        /// of EventSub's <c>GoalType</c>, and an author cannot be expected to guess Twitch's
        /// casing, so the only key they can bind at all is a predictable one. Case 1 (a slug the
        /// AUTHOR typed, published by a script's <c>overlay.publish</c>) still publishes verbatim
        /// — that rule is not weakened here, it simply does not apply where there is no author
        /// text. The duty this creates is documented, not coded: the Goal.Progress bubble in all
        /// four lang bundles tells the author an unrecognised type appears as
        /// <c>custom_&lt;lowercased_type&gt;</c> and must be typed in lower case, because
        /// <c>OverlayLiveStore</c> matches Ordinal and the reader folds only the reserved
        /// kinds.</para>
        ///
        /// <para>The <c>custom_</c> row is the load-bearing one: a goal type Twitch adds after
        /// this ships must still reach the overlay under a discoverable key rather than
        /// vanishing.</para>
        /// </summary>
        internal static string? KindForGoalType(string? goalType)
        {
            string t = (goalType ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length == 0) return null;

            // Twitch's documented channel.goal.* types are follow / subscription /
            // subscription_count / new_subscription / new_subscription_count / new_bit /
            // new_cheer. The two exact rows come first because they are exact; the two
            // substring rows cover every present and announced spelling of their family.
            if (t == "follow" || t == "followers") return KindFollower;
            if (t.Contains("subscription", StringComparison.Ordinal)) return KindSub;
            if (t.Contains("bit", StringComparison.Ordinal) || t.Contains("cheer", StringComparison.Ordinal))
                return KindBits;

            string slug = Slug(t);
            return slug.Length == 0 ? null : CustomKindPrefix + slug;
        }

        /// <summary>
        /// Reduces an already-lower-cased goal type to the channel's key charset
        /// <c>[a-z0-9_.-]</c>. Every other character becomes <c>_</c> rather than being dropped,
        /// so two different types cannot collapse onto one key.
        /// </summary>
        private static string Slug(string lowered)
        {
            var sb = new System.Text.StringBuilder(lowered.Length);
            foreach (char ch in lowered)
            {
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')
                          || ch == '_' || ch == '.' || ch == '-';
                sb.Append(ok ? ch : '_');
            }
            return sb.ToString();
        }

        // ── The charity campaign: the one MONEY goal ─────────────────────────

        /// <summary>
        /// Publishes a charity CAMPAIGN event into <c>goal.charity.*</c>.
        ///
        /// <para><c>label</c> always publishes. <c>progress</c> publishes whenever BOTH amounts
        /// were readable — the ratio survives any shared unit, so an unknown SCALE does not stop it.
        /// <c>current</c> and <c>target</c> publish only when the payload also states its own scale
        /// — see <see cref="TryResolveCharityAmounts"/> for the fallback and why it is the right
        /// one.</para>
        ///
        /// <para>★ The two conditions are DIFFERENT and that is the whole shape of this method:
        /// "the amounts are unreadable" kills the ratio too, while "the amounts are readable but
        /// their scale is not" kills only the two numeric readouts.</para>
        /// </summary>
        private static void PublishCharity(OverlayLiveStore store, string eventType, JsonElement data)
        {
            const string root = KeyPrefix + KindCharity + ".";

            bool haveAmounts = TryResolveCharityAmounts(
                data, out decimal rawCurrent, out decimal rawTarget,
                out int exponent, out bool scaleKnown, out string reasonCode, out string scaleReason);

            string currency = ProbeString(data, CharityCurrencyProbes);

            bool publishedNumbers = false;
            if (haveAmounts && scaleKnown)
            {
                if (TryMinorUnits(rawCurrent, out long minorCurrent)
                    && TryMinorUnits(rawTarget, out long minorTarget))
                {
                    // ★ BOTH sides through the SAME normalisation, with the SAME exponent and the
                    // SAME currency. This is the whole point of reading the raw payload rather than
                    // the resolved vars: DonationIngest.NormalizeMoneyVars rewrites only
                    // event.amount / event.amount_cents / event.currency, and charity.targetAmount is
                    // a bare string mapping on no normalisation path at all (a tree-wide search finds
                    // targetAmount only in PlatformEventCatalog). Taking `current` from the
                    // canonicalised var and `target` from the raw one gives a 100x ratio error inside
                    // a SINGLE event — precisely the failure a progress bar exists to avoid.
                    PublishNumber(store, root + FieldCurrent, MajorUnits(minorCurrent, exponent, currency));
                    PublishNumber(store, root + FieldTarget,  MajorUnits(minorTarget,  exponent, currency));
                    publishedNumbers = true;
                }
                else
                {
                    // A figure outside long's range cannot be re-expressed as minor units at all.
                    // Its own code, because every refusal that reaches NoteOnce needs one — see
                    // the keying note on NoteOnce.
                    reasonCode  = "charity-amount-out-of-range";
                    scaleReason = "carried an amount too large to re-express as minor units, so it "
                                + "could not be normalised without overflowing";
                }
            }

            if (!publishedNumbers)
            {
                // FALLBACK — current/target OMITTED.
                //
                // A numeric readout that is silently 100x wrong is worse than a blank one: a blank
                // field reads as missing data, which it is, whereas 260000 reads as a real figure.
                // Missing on those two pins is already an honest state the reader renders.
                //
                // Not cleared, either — a key a previous event legitimately published is left
                // alone rather than overwritten with null, because a payload shape does not flip
                // mid-stream and a null would report Active for nothing.
                NoteOnce(eventType, reasonCode, data, scaleReason);
            }

            // ★ progress publishes ONLY when the amounts were actually readable — the SAME
            // standard the two pins above are held to, and for a stronger reason: the ratio is
            // COMPUTED, not read, so publishing it here when nothing was readable publishes an
            // invented 0. It would OVERWRITE a correct bar (60% drops to 0 on one malformed
            // event), it would still report the root Active to liveGoalState (a confident empty
            // bar rather than declining to render), and because goalProgressOf gives a PUBLISHED
            // progress absolute priority over deriving one from current/target, the invented 0
            // would also close the reader's own fallback — leaving a 0 on screen with the real
            // numbers sitting right there under the root. The comment two blocks up already
            // refuses to write a null "that would report Active for nothing"; this is the same
            // refusal, applied to the field that was left out of it.
            //
            // An unknown SCALE does NOT reach here: a ratio stays correct as long as both sides
            // share a unit, so the bar keeps working through every refusal that still read both
            // amounts. Only an unreadable AMOUNT omits it.
            if (haveAmounts)
                PublishNumber(store, root + FieldProgress, Progress01(rawCurrent, rawTarget, true));

            // The label is READ, not computed, and comes off its own probes — it is independent of
            // the amounts, so it publishes on every branch. An empty one is the accurate reading
            // of a payload carrying no charity name, not a fabricated figure, and present-and-empty
            // is the convention the count path already uses so liveGoalState can tell a Hub that
            // spoke from one that never did.
            store.PublishString(root + FieldLabel, ProbeString(data, CharityLabelProbes), LiveSource);
        }

        /// <summary>
        /// Resolves the charity pair AND a single scale verdict for it.
        ///
        /// <para>The pair is always reported when it is readable (<c>haveAmounts</c>), because
        /// <c>progress</c> only needs the ratio. <paramref name="scaleKnown"/> is the separate,
        /// stricter question: can these two integers be turned into MAJOR units without guessing?
        /// Two payload shapes answer yes, and each is accepted only as a PAIR so the two sides
        /// can never be read on different scales:</para>
        ///
        /// <list type="number">
        /// <item>An explicit <c>decimal_places</c> / <c>decimalPlaces</c> exponent. Twitch's raw
        /// EventSub charity shape is an integer <c>value</c> plus this exponent and never a
        /// float. ONE exponent is applied to BOTH sides even when the payload repeats it per
        /// slot — a campaign has one currency, and using one divisor for both keeps the ratio
        /// right, which is the property that matters most.</item>
        /// <item>A paired integer <c>valueMinor</c> on BOTH sides — the shape Streamer.bot is
        /// documented to normalise charity into, and the one <c>DonationIngest</c>'s own primary
        /// path expects. "Minor units" names its own scale, so the exponent is the CURRENCY's
        /// canonical one. Refused when only ONE side has it: mixing a <c>valueMinor</c>
        /// numerator with a bare-integer denominator of unknown scale would re-introduce the
        /// exact 100x ratio error this method exists to prevent.</item>
        /// </list>
        ///
        /// <para>A bare integer pair with neither signal is genuinely ambiguous — 260000 may be
        /// 2600.00 or 260000 and nothing in the payload says which — so the verdict is no, and
        /// the caller omits the two numeric fields. An exponent paired with a NON-integral value
        /// is a self-contradicting payload (a normalised decimal shipped beside a raw exponent),
        /// which is also refused: honouring it would divide an already-major figure again.</para>
        ///
        /// <para>★ Every refusal reports a DISTINCT <paramref name="reasonCode"/>. That code is the
        /// diagnostic's dedupe key, so a shared one lets the FIRST refusal an event type hits mask
        /// every later, genuinely different one — see the keying note on <see cref="NoteOnce"/>.
        /// Each code names one defect with one fix; the human-readable <paramref name="reason"/> is
        /// what the log prints.</para>
        /// </summary>
        private static bool TryResolveCharityAmounts(
            JsonElement data,
            out decimal current, out decimal target,
            out int exponent, out bool scaleKnown, out string reasonCode, out string reason)
        {
            exponent   = 0;
            scaleKnown = false;
            reasonCode = string.Empty;
            reason     = string.Empty;

            bool haveCurrent = TryProbeNumber(data, CharityCurrentProbes, out current);
            bool haveTarget  = TryProbeNumber(data, CharityTargetProbes,  out target);

            // (1) An explicit exponent.
            if (TryProbeInt(data, CharityExponentProbes, out int places) && places >= 0 && places <= 9)
            {
                if (haveCurrent && haveTarget)
                {
                    if (IsIntegral(current) && IsIntegral(target))
                    {
                        exponent   = places;
                        scaleKnown = true;
                        return true;
                    }
                    reasonCode = "charity-scale-contradiction";
                    reason = "carried a decimal_places exponent beside a NON-integer amount, which "
                           + "contradicts itself (an already-scaled decimal cannot also be minor units). "
                           + "The bar still tracks progress (it is a ratio), but the numeric readout is "
                           + "left blank rather than divided a second time";
                    return true;
                }
                // haveAmounts means BOTH sides readable, everywhere and on every branch — the
                // caller feeds it straight to Progress01's bothPresent, and a branch that returned
                // "one side is enough" would hand the ratio a silent zero denominator.
                reasonCode = "charity-amounts-incomplete";
                reason = "carried a decimal_places exponent but not both a current and a target "
                       + "amount, so no progress ratio could be formed either; every goal.charity.* "
                       + "field is left as whatever a previous event published rather than "
                       + "overwritten with 0";
                return false;
            }

            // (2) A PAIRED integer valueMinor.
            bool minorCurrent = TryProbeNumber(data, CharityCurrentMinorProbes, out decimal mCur);
            bool minorTarget  = TryProbeNumber(data, CharityTargetMinorProbes,  out decimal mTgt);
            if (minorCurrent && minorTarget && IsIntegral(mCur) && IsIntegral(mTgt))
            {
                // The valueMinor pair supersedes the `value` pair for BOTH the published figures
                // and the ratio, so numerator and denominator can never come from two fields.
                current    = mCur;
                target     = mTgt;
                exponent   = DonationAmount.ExponentFor(ProbeString(data, CharityCurrencyProbes));
                scaleKnown = true;
                return true;
            }

            if (haveCurrent || haveTarget)
            {
                reasonCode = "charity-scale-ambiguous";
                reason = "carried no minor-unit exponent (decimal_places / decimalPlaces) and no paired "
                       + "valueMinor, so its amounts could be minor units or major units and nothing in "
                       + "the payload says which. The bar still tracks progress when both sides are "
                       + "readable (it is a ratio), but the numeric readout is left blank rather than "
                       + "possibly 100x wrong";
            }
            else
            {
                reasonCode = "charity-amounts-missing";
                reason = "carried no readable current/target amount at all (probes: "
                       + "charity.currentAmount.value / charity.amount.value / amount, "
                       + "charity.targetAmount.value / targetAmount), so no progress ratio could be "
                       + "formed either; every goal.charity.* number is left as whatever a previous "
                       + "event published rather than overwritten with 0";
            }

            return haveCurrent && haveTarget;
        }

        /// <summary>
        /// THE one normalisation call for charity money, used for current AND target.
        ///
        /// <para>Publishes MAJOR units — <c>2600.00</c>, not <c>260000</c>. Not a taste call:
        /// C1's script-var contract is already major (<c>event.amount</c> is the invariant major
        /// decimal and every consumer reads that form), so a future <c>goal.tip.*</c> producer
        /// will be major too. One <c>Goal.Progress</c> widget must not mean two unit systems
        /// depending on its Kind — and because the reader treats every kind identically, nothing
        /// downstream could detect the mismatch.</para>
        ///
        /// <para>Derived as <c>MinorUnits / 10^ExponentFor(Currency)</c> and NEVER as
        /// <c>DonationAmount.Value</c> verbatim: <c>FromDecimal</c> rounds only
        /// <c>MinorUnits</c> and stores <c>Value</c> UNROUNDED, so a payload of <c>5.12345</c>
        /// keeps <c>Value = 5.12345</c>, which the browser's formatter (maximumFractionDigits 3)
        /// silently renders as <c>5.123</c>. <c>ExponentFor</c> returns only 0 / 2 / 3, so the
        /// re-derived major value can never exceed 3 decimals and can never be rounded by that
        /// formatter.</para>
        /// </summary>
        private static decimal MajorUnits(long rawMinor, int exponent, string currency)
        {
            // FromMinorUnits re-expresses the pair in the CURRENCY's canonical exponent, so
            // MinorUnits below is comparable across sources even when the payload used a
            // different exponent than the currency's own.
            var amount = DonationAmount.FromMinorUnits(rawMinor, exponent, currency);
            int exp = DonationAmount.ExponentFor(amount.Currency);
            decimal scale = 1m;
            for (int i = 0; i < exp; i++) scale *= 10m;
            return amount.MinorUnits / scale;
        }

        // ── Numeric helpers ─────────────────────────────────────────────────

        /// <summary>
        /// The contract's progress formula: <c>target &gt; 0 ? clamp01(current / target) : 0</c>.
        /// A target of 0 or below reads as 0 — never as finished, because 0 out of 0 is not
        /// complete — and the clamp means a current above target reads as 1 rather than driving a
        /// bar past its own width.
        /// </summary>
        internal static decimal Progress01(decimal current, decimal target, bool bothPresent)
        {
            if (!bothPresent || target <= 0m) return 0m;
            decimal p = current / target;
            if (p <= 0m) return 0m;
            if (p >= 1m) return 1m;
            // Six places is well past a bar's addressable width and keeps the coalescing compare
            // from churning on the last digits of a 28-digit decimal quotient.
            return Math.Round(p, 6, MidpointRounding.AwayFromZero);
        }

        private static bool IsIntegral(decimal d) => d == decimal.Truncate(d);

        private static bool TryMinorUnits(decimal raw, out long minor)
        {
            minor = 0;
            if (raw > long.MaxValue || raw < long.MinValue) return false;
            minor = (long)decimal.Truncate(raw);
            return true;
        }

        /// <summary>
        /// Publishes a real JSON NUMBER. <c>PublishString</c> would make it a JSON string, and
        /// the browser's formatters only thousands-group actual numbers — a "2600" published as
        /// text renders as 2600 forever.
        /// </summary>
        // decimal, not double, all the way to the wire. A JsonValue built from a double would
        // reintroduce base-2 formatting artefacts (0.6200000000000001) into a value the store
        // coalesces on by its JSON TEXT — so a stable ratio would churn a frame every tick.
        // Decimal division already yields the minimal scale (260000m / 100m is 2600, not
        // 2600.00), so no trailing-zero trimming is needed here.
        private static void PublishNumber(OverlayLiveStore store, string key, decimal value)
            => store.Publish(key, JsonValue.Create(value), LiveSource);

        // ── Payload probing ─────────────────────────────────────────────────
        //
        // Resolves one candidate three ways, in order: a nested walk down a dotted path, the
        // whole dotted string as ONE flat key, then the plain key. This mirrors both existing
        // resolvers (PlatformEventVarResolver.TryResolveElement and DonationIngest.TryResolve) —
        // it is re-implemented rather than reused because both are `internal` to assemblies that
        // grant InternalsVisibleTo only to the test project, so neither is reachable from a new
        // Hub type. SB's trigger-variable export flattens nested groups into dotted key names
        // while its WebSocket payload nests, so both shapes must hit.

        private static bool TryResolve(JsonElement data, string probe, out JsonElement found)
        {
            found = default;
            if (data.ValueKind != JsonValueKind.Object) return false;

            if (probe.IndexOf('.') >= 0)
            {
                var cur = data;
                bool ok = true;
                foreach (var seg in probe.Split('.'))
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) { ok = false; break; }
                }
                if (ok) { found = cur; return true; }
                return data.TryGetProperty(probe, out found);
            }

            return data.TryGetProperty(probe, out found);
        }

        private static string ProbeString(JsonElement data, string[] probes)
        {
            if (data.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var p in probes)
            {
                if (!TryResolve(data, p, out var el)) continue;
                switch (el.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                        break;
                    case JsonValueKind.Number:
                        return el.GetRawText();
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Reads a numeric field as an invariant <see cref="decimal"/>, accepting both a JSON
        /// number and a JSON string (SB's trigger-variable surface stringifies everything).
        /// decimal, not double, because this feeds money: a base-2 round trip on 26.01 is exactly
        /// the drift <see cref="DonationAmount"/> was introduced to remove.
        /// </summary>
        private static bool TryProbeNumber(JsonElement data, string[] probes, out decimal value)
        {
            value = 0m;
            if (data.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in probes)
            {
                if (!TryResolve(data, p, out var el)) continue;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out value)) return true;
                if (el.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return true;
            }
            value = 0m;
            return false;
        }

        private static bool TryProbeInt(JsonElement data, string[] probes, out int value)
        {
            value = 0;
            if (data.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in probes)
            {
                if (!TryResolve(data, p, out var el)) continue;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
                if (el.ValueKind == JsonValueKind.String &&
                    int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return true;
            }
            value = 0;
            return false;
        }

        // ── Probe-drift diagnostic ──────────────────────────────────────────
        //
        // Same discipline as DonationIngest.NoteProbeMissOnce, and for the same reason: every
        // field name above is a best-effort guess against a schema Streamer.bot does not publish,
        // so when a goal arrives and the producer cannot read what it needs, print the payload's
        // ACTUAL field names ONCE. That turns a wrong probe into a one-line fix from a single
        // live capture instead of a silently blank readout. ALL SIX wired events reach this — the
        // count path's own amount misses included, which were silent until the P2 fix and are the
        // three whose probe names are least confirmed against a live wire.
        //
        // ★ Keyed by (event type, REASON CODE), and every call site therefore passes a code that
        // names ONE defect. That is not decoration: a shared code makes the key identical for two
        // genuinely different misses, so the first one an event type ever hits permanently masks
        // the second — the exact failure this keying exists to prevent. If you add a refusal, give
        // it its own code.
        //
        // The CONSEQUENCE belongs in `what`, not here: some refusals keep the bar moving (the
        // amounts were readable, only their scale was not — a ratio survives any shared unit) and
        // some do not (nothing was readable, so nothing is published and the previous values
        // stand). A single boilerplate sentence claiming either one would be wrong half the time.

        private static readonly ConcurrentDictionary<string, byte> _warned = new();
        private const int MaxReportedFields = 24;

        private static void NoteOnce(string eventType, string reasonCode, JsonElement data, string what)
        {
            if (!_warned.TryAdd(eventType + "|" + reasonCode, 0)) return;
            try
            {
                var names = new List<string>();
                if (data.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in data.EnumerateObject())
                    {
                        names.Add(prop.Name);
                        if (names.Count >= MaxReportedFields) break;
                    }
                }
                GlobalLogger.Log(
                    $"Goal channel: {eventType} {what}. "
                    + $"Payload fields: [{string.Join(", ", names)}] — one-time notice per event type "
                    + $"and reason.",
                    "GoalChannelProducer", LogLevel.Communication);
            }
            catch { /* diagnostic only */ }
        }

        /// <summary>Test seam — clears the once-per-event-type diagnostic gate.</summary>
        internal static void ResetForTests() => _warned.Clear();
    }
}
