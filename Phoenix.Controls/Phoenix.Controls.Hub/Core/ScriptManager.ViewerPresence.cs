using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: viewer presence — the suite's ONE GetActiveViewers round-trip,
    // the on-demand per-login role lookup behind it, and the seam wiring that hands
    // both to ViewerPresenceService (and through it to LoyaltyService).
    //
    // ── Why this file exists ─────────────────────────────────────────────────
    // There used to be THREE GetActiveViewers implementations in this class, and
    // they disagreed with each other in ways that were invisible until a live
    // stream produced the mismatch:
    //
    //   • ResolveEntrantSubscribersAsync (Giveaway, Stage 1) did not lowercase the
    //     login, so its map was keyed on whatever casing Streamer.bot sent.
    //   • FetchLoyaltyActiveViewersAsync lowercased, but read only `subscribed` —
    //     mod and VIP were hard-false, which is a different claim from "unknown"
    //     and is exactly the claim that erases a moderator (see RolesKnown below).
    //   • twitch.get_viewers had no IsConnected guard — and SendAndWaitAsync only
    //     resolves when a correlated reply lands or the timeout fires, so a call
    //     against a dead socket cost the full 5 seconds every time. It also had no
    //     ValueKind == Array check before EnumerateArray (a `viewers` value that was
    //     not an array threw straight into the catch and returned nobody), a
    //     hand-rolled request id, and no lowercasing.
    //
    // Three callers asking the platform "who is watching?" milliseconds apart also
    // get three different answers, which matters because two of those answers are
    // spent on the same viewer: the Loyalty payout and the watch-minute accrual
    // must agree about who was present, or a viewer is paid for a minute the watch
    // table does not record (or vice versa). One implementation cannot disagree
    // with itself.
    //
    // ── The role field is the delicate part ──────────────────────────────────
    // `role` on a GetActiveViewers entry is UNPUBLISHED in shape. It is parsed
    // defensively here (number OR string), and anything unrecognised sets NEITHER
    // flag and names itself once in the System log, so the real shape can be read
    // off a live stream instead of guessed a second time.
    //
    // ★★ ActiveViewer.RolesKnown is what keeps that honesty downstream. It is true
    // ONLY when the viewer's own object carried a usable role field — never merely
    // because the request succeeded. ViewerPresenceService.RecordRoles keeps the
    // previous mod/VIP answer when RolesKnown is false, so a role-less sweep can no
    // longer overwrite the authoritative flags a viewer's own chat messages already
    // proved. Report a role-less sweep as "everyone is a non-mod" and every
    // moderator in the channel silently loses their mod-gated commands until they
    // type again.
    public partial class ScriptManager
    {
        // How stale a shared presence sample may be before a Loyalty payout tick
        // pays for its own round-trip. The payout cadence is minutes; the sampler's
        // own cadence is 15-300s (AppConfig.ViewerPresencePollSeconds, clamped), so
        // 90s covers the default cadence — but it is a FLOOR, not the whole answer:
        // the wiring below widens it to the live cadence plus a margin, because a
        // cadence configured above 90s would otherwise make every payout tick pay
        // for its own fetch again, and the payout and the accrual would once more
        // disagree about who was present (the exact divergence the shared sample
        // exists to prevent).
        internal const int LoyaltyPresenceMaxAgeSeconds = 90;

        /// <summary>
        /// Hands the Hub's Streamer.bot plumbing to the services that may not touch
        /// it themselves. Called from <c>RegisterLoyaltyCommands</c> — beside the
        /// rest of the Loyalty seam block, because <c>ActiveViewersProvider</c> is
        /// one of the four assignments made here and splitting the two halves across
        /// two call sites is how they drift apart.
        /// </summary>
        internal void RegisterViewerPresenceSeams()
        {
            var presence = ViewerPresenceService.Instance;

            // The ONE sweep. The service holds no socket (pillar rule: only the
            // Hub's Streamer.bot plumbing talks to Streamer.bot).
            presence.FetchProvider = FetchActiveViewersAsync;

            // Bot accounts — REUSED from the Loyalty seam block rather than
            // duplicated, so "which accounts are bots" has one answer for the payout
            // exclusion, the automod exclusion and the watch-time exclusion alike.
            presence.BotAccountsProvider = GetLoyaltyBotAccounts;

            // Third role source (after the chat path and the sweep): a per-login
            // platform lookup for a viewer who neither typed nor appeared in a
            // sample. The service rate-limits it and remembers its misses; this
            // provider just does one round-trip and answers honestly.
            presence.RoleFallbackProvider = LookupPlatformRolesAsync;

            // ★ Loyalty reads the SHARED sample instead of opening its own fetch,
            // and a sample up to 90s old is BETTER here than a fresh one — not a
            // compromise. The payout tick and the watch-minute accrual both answer
            // "who was watching for this interval"; two fetches milliseconds apart
            // return two different lists (someone closed the tab, someone opened
            // it), and the two features would then pay and record different people
            // for the same minute. Reading one sample makes the payout and the watch
            // table agree by construction. The draw in ScriptManager.Giveaway.cs is
            // the deliberate exception — see the comment there.
            LoyaltyService.Instance.ActiveViewersProvider = () =>
                presence.GetOrFetchAsync(TimeSpan.FromSeconds(
                    // Live-read per call: max(90, cadence + 15s jitter margin), so a
                    // slow configured cadence keeps the tick on the shared sample.
                    Math.Max(LoyaltyPresenceMaxAgeSeconds, presence.PollSeconds + 15)));
        }

        // ── The one sweep ───────────────────────────────────────────────────
        /// <summary>
        /// The suite's single <c>GetActiveViewers</c> round-trip: every login the
        /// platform currently reports as present, lowercased, with the subscriber
        /// flag, the display name and whatever the <c>role</c> field could be made
        /// to say.
        ///
        /// <para>NULL means the sweep FAILED — disconnected, timed out, or the
        /// payload carried no readable <c>viewers</c> array. An EMPTY list means the
        /// platform genuinely reported nobody. The distinction is load-bearing:
        /// ViewerPresenceService keeps its previous sample and accrual anchor on a
        /// failed sweep, where an empty one is a real observation it publishes.</para>
        /// </summary>
        internal async Task<IReadOnlyList<ActiveViewer>?> FetchActiveViewersAsync()
        {
            // Disconnected is answered locally: SendAndWaitAsync on a dead socket
            // costs the full 5s timeout, and the sampler runs on a timer.
            if (!WS.Instance.IsConnected) return null;

            string reqId = WS.NewRequestId("phx-viewers");
            string response = await WS.Instance.SendAndWaitAsync(
                $@"{{""request"":""GetActiveViewers"",""id"":""{reqId}""}}",
                reqId, 5000).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response)) return null;

            var list = new List<ActiveViewer>();
            // Whether ANY entry carried a role field with something in it (a missing
            // field and an explicit null are the same fact here: nothing was said).
            // A payload that carries none is a legitimate shape — roles then come from
            // the chat path and the on-demand lookup — but it is worth saying out loud
            // once, because it is the difference between "lurkers have no rank yet" and
            // "roles are broken". A field that IS present but unreadable is a different
            // diagnosis and gets its own note from WarnUnknownRole.
            bool sawRoleValue = false;
            // Whether a well-formed `viewers` array was found at all — the line
            // between "the platform answered (possibly: nobody)" and "this payload
            // said nothing", which the null contract reports as a failed sweep.
            bool sawViewersArray = false;

            try
            {
                using var doc = JsonDocument.Parse(response);
                // The response carries `viewers[]` (NOT `users[]`). GetUsers — the
                // request twitch.get_viewers used to send — is not a real
                // Streamer.bot request; only the external StreamSimulator ever
                // answered it, which is why a cron that worked under the sim awarded
                // nobody anything against a live Streamer.bot.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("viewers", out var viewers)
                    && viewers.ValueKind == JsonValueKind.Array)
                {
                    sawViewersArray = true;
                    foreach (var v in viewers.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Object) continue;

                        // A login is the identity everything downstream keys on
                        // (wallet rows, watch-time rows, the role cache), so an
                        // entry without one is not a viewer we can do anything with.
                        if (!v.TryGetProperty("login", out var loginEl)
                            || loginEl.ValueKind != JsonValueKind.String
                            || loginEl.GetString() is not { Length: > 0 } login)
                            continue;

                        bool sub = v.TryGetProperty("subscribed", out var subEl) && JsonFlagIsTrue(subEl);

                        string display = v.TryGetProperty("display", out var dispEl)
                                      && dispEl.ValueKind == JsonValueKind.String
                            ? (dispEl.GetString() ?? string.Empty).Trim()
                            : string.Empty;

                        bool isMod = false, isVip = false, rolesKnown = false;
                        if (v.TryGetProperty("role", out var roleEl))
                        {
                            // Something was SAID here — recorded separately from
                            // whether it could be understood.
                            if (roleEl.ValueKind != JsonValueKind.Null) sawRoleValue = true;
                            rolesKnown = TryReadPlatformRole(roleEl, out isMod, out isVip);
                        }

                        list.Add(new ActiveViewer(
                            login.ToLowerInvariant(), sub, isMod, isVip, display, rolesKnown));
                    }
                }
            }
            catch (Exception ex)
            {
                // Log and keep what was already parsed rather than discarding the
                // whole sweep: a single malformed entry late in a 300-viewer payload
                // must not read as "nobody is watching", which would zero a payout
                // tick and break the watch-time accrual anchor.
                GlobalLogger.Log($"Viewer sweep: GetActiveViewers parse failed: {ex.Message}",
                    "Script", LogLevel.CriticalError);
            }

            // No readable viewers array anywhere in the payload = the sweep said
            // nothing — report failure, never "nobody is watching".
            if (!sawViewersArray) return null;

            if (list.Count > 0 && !sawRoleValue
                && Interlocked.Exchange(ref _viewerRoleFieldMissingWarned, 1) == 0)
            {
                GlobalLogger.Log(
                    "Viewer sweep: Streamer.bot's GetActiveViewers reply carries no \"role\" value, so moderator " +
                    "and VIP for viewers who have not chatted are resolved from chat messages and the " +
                    "\"Phoenix: Get User\" lookup instead. Nothing is broken by this — it is noted once so the " +
                    "payload shape is on record. (One-time note.)",
                    "Script", LogLevel.System);
            }

            return list;
        }

        // ── The `role` field ────────────────────────────────────────────────
        // Streamer.bot's Twitch role enum is documented as 1=Viewer, 2=VIP,
        // 3=Moderator, 4=Broadcaster — but the field is not part of any published
        // GetActiveViewers schema, and SB's "Auto Type" toggle decides whether it
        // arrives as a JSON number or as a string (the same toggle JsonFlagIsTrue
        // already copes with for `subscribed`). Both are accepted, plus the obvious
        // word spellings, because guessing wrong here costs a moderator their
        // commands rather than a log line.
        //
        // Returns TRUE only when the value was actually understood — that boolean
        // becomes ActiveViewer.RolesKnown, and the presence service uses it to
        // decide whether it may overwrite mod/VIP at all.
        private static bool TryReadPlatformRole(JsonElement roleEl, out bool isMod, out bool isVip)
        {
            isMod = false;
            isVip = false;

            switch (roleEl.ValueKind)
            {
                case JsonValueKind.Number:
                    if (roleEl.TryGetInt64(out long n)) return TryReadRoleNumber(n, ref isMod, ref isVip);
                    break;

                case JsonValueKind.String:
                    string raw = (roleEl.GetString() ?? string.Empty).Trim();
                    // Present but empty says nothing at all — the honest answer is
                    // "not known", and it is not a shape worth reporting.
                    if (raw.Length == 0) return false;
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sn))
                        return TryReadRoleNumber(sn, ref isMod, ref isVip);
                    switch (raw.ToLowerInvariant())
                    {
                        case "viewer":
                            return true;                       // known, and neither flag
                        case "vip":
                            isVip = true; return true;
                        case "mod":
                        case "moderator":
                            isMod = true; return true;
                        case "broadcaster":
                            // The broadcaster holds every moderator power on their
                            // own channel, so a mod gate that refused them would be
                            // absurd. Kept identical in LookupPlatformRolesAsync
                            // below, so the answer does not depend on which of the
                            // three role sources happened to arrive first.
                            isMod = true; return true;
                    }
                    break;

                case JsonValueKind.Null:
                    return false;                              // explicitly "no answer"
            }

            WarnUnknownRoleShape(roleEl);
            return false;
        }

        private static bool TryReadRoleNumber(long value, ref bool isMod, ref bool isVip)
        {
            switch (value)
            {
                case 1: return true;                           // Viewer
                case 2: isVip = true; return true;             // VIP
                case 3: isMod = true; return true;             // Moderator
                case 4: isMod = true; return true;             // Broadcaster ⇒ mod
                default:
                    // Includes 0, which the documented enum does not use. If a live
                    // Streamer.bot turns out to send it for ordinary viewers, the
                    // one-time note below is what says so — and until then nobody is
                    // demoted, because an unread value sets neither flag.
                    WarnUnknownRoleNumber(value);
                    return false;
            }
        }

        // One note per process for each of the two "we could not read the role"
        // shapes, so a live stream produces the evidence without producing a log
        // line per viewer per sample.
        private static int _viewerRoleShapeWarned;
        private static int _viewerRoleFieldMissingWarned;

        private static void WarnUnknownRoleShape(JsonElement roleEl)
        {
            string raw;
            try { raw = roleEl.GetRawText(); }
            catch { raw = roleEl.ValueKind.ToString(); }
            WarnUnknownRole(Truncate(raw, 120));
        }

        private static void WarnUnknownRoleNumber(long value)
            => WarnUnknownRole(value.ToString(CultureInfo.InvariantCulture));

        private static void WarnUnknownRole(string raw)
        {
            if (Interlocked.Exchange(ref _viewerRoleShapeWarned, 1) != 0) return;
            GlobalLogger.Log(
                $"Viewer sweep: unrecognised \"role\" value {raw} in Streamer.bot's GetActiveViewers reply. " +
                "Moderator and VIP were left exactly as the chat path and the \"Phoenix: Get User\" lookup " +
                "found them for those viewers — nobody is demoted by a value Phoenix cannot read. Please pass " +
                "this value on so the role shape can be supported. (One-time note.)",
                "Script", LogLevel.System);
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";

        // ── On-demand role lookup (the third source) ────────────────────────
        /// <summary>
        /// One login's platform standing, straight from the "Phoenix: Get User" data
        /// action — the same round-trip the giveaway's Stage-2 subscriber fallback
        /// uses. Wired into <see cref="ViewerPresenceService.RoleFallbackProvider"/>.
        ///
        /// <para>NULL means "Streamer.bot could not answer" (disconnected, action
        /// missing, timed out, or the reply carried nothing about this user). That
        /// distinction is the whole point of the nullable return: the service
        /// remembers a miss and retries later, where a false-everywhere
        /// <see cref="PlatformRoles"/> would be cached as the authoritative claim
        /// that this viewer is not a moderator.</para>
        /// </summary>
        internal async Task<PlatformRoles?> LookupPlatformRolesAsync(string login)
        {
            string user = (login ?? string.Empty).Trim();
            if (user.Length == 0) return null;

            var g = await FetchActionGlobalsAsync("viewer role lookup", PhxSbActions.GetUser,
                new Dictionary<string, string> { ["user"] = user }).ConfigureAwait(false);
            if (g is null) return null;

            string G(string k) => g.TryGetValue(k, out var v) ? v : "";
            string resolved = G("phx_user_login");

            // The action echoes phx_req whether or not it found the user, so a
            // non-null map is NOT by itself an answer about this login. Require
            // either a resolved login or at least one role global to be present;
            // otherwise report the miss rather than inventing a viewer with no roles.
            if (resolved.Length == 0
                && !g.ContainsKey("phx_user_mod")
                && !g.ContainsKey("phx_user_sub")
                && !g.ContainsKey("phx_user_vip"))
                return null;

            // NormBool, not a raw string compare: the pack's sub-actions stringify
            // these flags as "True"/"true"/"1"/"yes" depending on how they were
            // built, and ApplyUserGlobals reads the very same globals through it.
            bool isSub = NormBool(G("phx_user_sub")) == "true";
            bool isVip = NormBool(G("phx_user_vip")) == "true";
            bool isMod = NormBool(G("phx_user_mod")) == "true";

            // Same broadcaster⇒mod rule the sweep's role 4 applies, so a channel
            // owner's rank does not depend on which source answered first.
            if (IsConfiguredBroadcaster(resolved.Length > 0 ? resolved : user)) isMod = true;

            return new PlatformRoles(isSub, isMod, isVip);
        }
    }
}
