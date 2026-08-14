using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // AlertsService — the runtime for the Alerts pre-build tool (StreamElements-style
    // event responses): per event family (Follow / Sub / Gift Sub / Gift Bomb / Bits /
    // Raid) the streamer activates a response with one or more SIZE TIERS; each tier
    // posts a templated chat line and can fire a Visual.Trigger (layer + trigger) over
    // the SAME pipeline a graph uses. Raids can auto-shoutout.
    //
    // Purely event-driven (no tick loop): ScriptManager.ExecuteGenericEventAsync taps
    // it in the pre-guard region beside Timer/Loyalty with the already-built generic
    // event vars. Master Config.Enabled gates behaviour; default OFF (opt-in).
    //
    // Hub-state-agnostic: chat/shoutout/visual all go through seams wired by
    // ScriptManager.Alerts.cs to the blessed send cores + the shared visual fan-out.
    public sealed class AlertsService
    {
        private readonly DB _db;
        public AlertsService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static AlertsService? _instance;
        private static readonly object _instanceGate = new();
        public static AlertsService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new AlertsService(DB.Instance);
            }
        }

        // ── Seams (wired by ScriptManager.Alerts.cs; null-safe) ──────────────
        /// <summary>Post one resolved line on the given chat platform token
        /// ("twitch"/"youtube"/"kick" — see ChatPlatforms).</summary>
        public Func<string, string, Task>? SendChat { get; set; }
        /// <summary>Fire a Twitch shoutout for the given login.</summary>
        public Func<string, Task>? Shoutout { get; set; }
        /// <summary>Fire a visual trigger on every widget of the layer owning the
        /// trigger. Args: (layerId, triggerName, eventData).</summary>
        public Func<string, string, Dictionary<string, string>, Task>? FireVisual { get; set; }

        // ── Config (swapped wholesale; volatile ⇒ visible on the event path) ─
        private volatile AlertsConfig _config = new();
        public AlertsConfig Config => _config;
        /// <summary>Master gate — true only when the streamer enabled the tool.</summary>
        public bool Active => _config.Enabled;

        // ── Change events (crash-safe SafeEvent; UI-side MUST marshal) ───────
        public event EventHandler? ConfigChanged;
        public event EventHandler? RuntimeChanged;
        private void RaiseConfigChanged()
            => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "AlertsService", "ConfigChanged");
        private void RaiseRuntimeChanged()
            => SafeEvent.Raise(RuntimeChanged, this, EventArgs.Empty, "AlertsService", "RuntimeChanged");

        // Monotonic all-session alert total (drives the "alerts fired" stat tile).
        private int _alertsFiredEver;
        public int TotalAlertsFired() => _alertsFiredEver;

        // ── Event families ───────────────────────────────────────────────────

        /// <summary>The response families the tool configures. Values double as
        /// the Args1 KIND label sent to the visual trigger (upper-cased).
        /// <para>APPEND new members at the END. The label a family emits is its
        /// contract with every alert widget graph a streamer has already built, so
        /// reordering or renaming is a breaking change to authored content.</para></summary>
        public enum AlertFamily
        {
            Follow, Sub, GiftSub, GiftBomb, Cheer, Raid,
            // Added with the Twitch event-surface completion.
            Charity, ShoutoutReceived, WatchStreak, Tip,
        }

        /// <summary>Maps an inbound platform event type to its alert family plus the
        /// var key holding the event's SIZE metric ("" = no size, tiers key off 0).
        /// Only source-verified event types are listed — an unmapped event is simply
        /// not an alert.</summary>
        internal static bool TryMapEvent(string eventType, out AlertFamily family, out string sizeKey)
        {
            switch (eventType)
            {
                // Follow
                case "Twitch.Follow":
                case "Kick.Follow":
                    family = AlertFamily.Follow; sizeKey = ""; return true;

                // Sub / Resub — size = cumulative months (a fresh sub reads 0/1).
                case "Twitch.Sub":
                case "Twitch.Resub":
                case "Twitch.ReSub":
                case "YouTube.NewSponsor":
                case "YouTube.MemberMileStone":
                case "Kick.Subscription":
                case "Kick.Resubscription":
                    family = AlertFamily.Sub; sizeKey = "user.sub_months"; return true;

                // Single gifted sub
                case "Twitch.GiftSub":
                case "YouTube.GiftMembershipReceived":
                case "Kick.GiftSubscription":
                    family = AlertFamily.GiftSub; sizeKey = ""; return true;

                // Gift bomb — size = gift count.
                case "Twitch.GiftBomb":
                case "YouTube.MembershipGift":
                case "Kick.MassGiftSubscription":
                    family = AlertFamily.GiftBomb; sizeKey = "user.count"; return true;

                // Bits / paid cheers — size = amount.
                case "Twitch.Cheer":
                    family = AlertFamily.Cheer; sizeKey = "user.bits"; return true;
                case "Kick.KicksGifted":
                case "YouTube.SuperChat":
                    family = AlertFamily.Cheer; sizeKey = "event.amount"; return true;

                // Raid — size = viewer count.
                case "Twitch.Raid":
                    family = AlertFamily.Raid; sizeKey = "user.viewers"; return true;

                // Charity — size = the donated amount. Rides the canonical money
                // path, so event.amount is already an invariant decimal here.
                case "Twitch.CharityDonation":
                case "Streamlabs.CharityDonation":
                    family = AlertFamily.Charity; sizeKey = "event.amount"; return true;

                // Shoutout RECEIVED (someone shouted this channel out). The
                // shoutout this channel CREATES is deliberately not an alert —
                // the streamer performed it, so announcing it back is noise.
                case "Twitch.ShoutoutReceived":
                    family = AlertFamily.ShoutoutReceived; sizeKey = "event.viewercount"; return true;

                // Watch streak — size = consecutive broadcasts watched.
                case "Twitch.WatchStreak":
                    family = AlertFamily.WatchStreak; sizeKey = "event.streak"; return true;

                // Tips from every third-party broker collapse into ONE family:
                // a streamer configures "what happens on a tip" once, not ten
                // times. event.source carries which broker it came from for
                // anyone who wants to branch on it.
                case "Streamlabs.Donation":
                case "StreamElements.Tip":
                case "Kofi.Donation":
                case "TipeeeStream.Donation":
                case "DonorDrive.Donation":
                case "Fourthwall.Donation":
                case "Pallygg.CampaignTip":
                    family = AlertFamily.Tip; sizeKey = "event.amount"; return true;

                default:
                    family = default; sizeKey = ""; return false;
            }
        }

        // ★ Both maps below used to END in a `_ =>` arm that returned the RAID
        // value. That reads as a harmless default but is a trap: a family added to
        // the enum without touching these would silently read and write the RAID
        // CARD'S CONFIG and emit Args1="RAID" to the widget — with no compiler
        // warning (the switch is exhaustive by construction) and no failing test.
        // Raid is now an explicit case in both, and the discard is a distinct
        // never-the-raid-card value, so the next family added without updating
        // these fails loudly instead of impersonating raids.
        private AlertEventConfig ConfigFor(AlertFamily family) => family switch
        {
            AlertFamily.Follow           => _config.Follow,
            AlertFamily.Sub              => _config.Sub,
            AlertFamily.GiftSub          => _config.GiftSub,
            AlertFamily.GiftBomb         => _config.GiftBomb,
            AlertFamily.Cheer            => _config.Cheer,
            AlertFamily.Raid             => _config.Raid,
            AlertFamily.Charity          => _config.Charity,
            AlertFamily.ShoutoutReceived => _config.ShoutoutReceived,
            AlertFamily.WatchStreak      => _config.WatchStreak,
            AlertFamily.Tip              => _config.Tip,
            // An unmapped family is a bug, not a raid. A disabled config means the
            // family simply never fires, which is the safe failure.
            _                            => new AlertEventConfig(),
        };

        internal static string KindLabel(AlertFamily family) => family switch
        {
            AlertFamily.Follow           => "FOLLOW",
            AlertFamily.Sub              => "SUBSCRIPTION",
            AlertFamily.GiftSub          => "GIFT SUB",
            AlertFamily.GiftBomb         => "GIFT SUBS",
            AlertFamily.Cheer            => "BITS",
            AlertFamily.Raid             => "RAID",
            AlertFamily.Charity          => "CHARITY",
            AlertFamily.ShoutoutReceived => "SHOUTOUT",
            AlertFamily.WatchStreak      => "WATCH STREAK",
            AlertFamily.Tip              => "TIP",
            _                            => "ALERT",
        };

        /// <summary>Pure tier pick (extracted for unit testing): the ENABLED tier with
        /// the highest MinValue that is &lt;= size; ties resolve to the first in list
        /// order. Null when nothing matches.</summary>
        internal static AlertTier? PickTier(List<AlertTier> tiers, int size)
        {
            AlertTier? best = null;
            foreach (var t in tiers)
            {
                if (t is null || !t.Enabled) continue;
                if (t.MinValue > size) continue;
                if (best is null || t.MinValue > best.MinValue) best = t;
            }
            return best;
        }

        /// <summary>Parses the event's size metric ("12"/"12.5" → 12); 0 on absence.</summary>
        internal static int ParseSize(Dictionary<string, string> vars, string sizeKey)
        {
            if (string.IsNullOrEmpty(sizeKey)) return 0;
            if (!vars.TryGetValue(sizeKey, out var raw) || string.IsNullOrWhiteSpace(raw)) return 0;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                // A double-to-int cast is UNSPECIFIED in an unchecked context once the
                // value leaves int range (and for NaN), so range-gate first: a payload
                // carrying "1e30" is nonsense as a size, and 0 = "no size" is the same
                // answer an absent metric gives. NaN fails both comparisons → 0.
                double floored = Math.Floor(d);
                if (floored >= int.MinValue && floored <= int.MaxValue) return (int)floored;
            }
            return 0;
        }

        /// <summary>The acting user for an alert: user.name where the event has one,
        /// falling back to the gifter then the recipient (Kick/YouTube gift events
        /// carry only those — see PlatformEventCatalog).</summary>
        internal static string ResolveActingUser(Dictionary<string, string> vars)
        {
            string V(string key) => vars.TryGetValue(key, out var v) ? v : "";
            string u = V("user.name");
            if (string.IsNullOrWhiteSpace(u)) u = V("user.gifter");
            if (string.IsNullOrWhiteSpace(u)) u = V("user.recipient");
            return u;
        }

        // One compiled scan of the whole template — chained string.Replace calls each
        // re-scan the ENTIRE string, so a value substituted by an earlier call is fair
        // game for a later one (a gifter literally named "{recipient}" used to get
        // rewritten). Same expression + MatchEvaluator shape as
        // CustomCommandsService.TokenRx.
        private static readonly Regex TokenRx = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

        /// <summary>Token substitution for the alert text. Documented in the tool UI —
        /// keep the two lists in lockstep. ONE pass: every token — viewer-controlled
        /// ({user}/{gifter}/{recipient}/{message}) or not — is resolved by the same
        /// evaluator and its value is never re-scanned, so a display name or chat body
        /// that spells a token comes through verbatim. An unknown token is left as the
        /// streamer authored it.</summary>
        internal static string ResolveTokens(string template, Dictionary<string, string> vars, int size)
        {
            if (string.IsNullOrEmpty(template)) return "";
            var map = vars ?? new Dictionary<string, string>();
            return TokenRx.Replace(template, m => ResolveToken(m.Value, m.Groups[1].Value, map, size));
        }

        private static string ResolveToken(string original, string inner, Dictionary<string, string> vars, int size)
        {
            string V(string key) => vars.TryGetValue(key, out var v) ? v : "";
            switch ((inner ?? "").Trim().ToLowerInvariant())
            {
                case "tier":      return V("user.tier");
                case "months":    return V("user.sub_months");
                case "count":     return V("user.count");
                case "viewers":   return V("user.viewers");
                case "bits":      return V("user.bits");
                case "platform":  return V("user.platform");
                // {amount} renders the CANONICAL amount when the event carries one,
                // falling back to the tier-size integer otherwise.
                //
                // Why this is not just `size`: size comes from ParseSize, which
                // floors to int because tier thresholds are integers. That is right
                // for bits, gift counts and raid viewers — all genuinely whole
                // numbers — but wrong for money, where it announced a EUR 2.50 tip
                // as "2". event.amount is the invariant decimal written by
                // DonationIngest, so money keeps its cents while every existing
                // integer family renders exactly as before.
                case "amount":
                {
                    string canonical = V("event.amount");
                    return canonical.Length > 0 ? canonical : size.ToString(CultureInfo.InvariantCulture);
                }
                case "currency":  return V("event.currency");
                case "source":    return V("event.source");
                case "user":      return ResolveActingUser(vars);
                case "gifter":    return V("user.gifter");
                case "recipient": return V("user.recipient");
                case "message":   return V("user.message");
                default:          return original;   // not ours — leave it as authored
            }
        }

        /// <summary>True when the name belongs to the channel itself — the broadcaster
        /// or one of the configured bot accounts. <c>AppConfig.BotUsername</c> is a
        /// COMMA-SEPARATED list, split/trimmed/compared case-insensitively exactly like
        /// <c>ScriptManager.GetLoyaltyBotAccounts</c>; read per call so a Settings edit
        /// lands without a Hub restart. Best-effort by name: raid payloads carry a
        /// display name, so this is the same weak comparison the WS self-guard falls
        /// back to.</summary>
        internal static bool IsOwnAccount(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return false;
            string name = user.Trim();

            string broadcaster = ConfigManager.Current?.BroadcasterUsername ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(broadcaster)
                && name.Equals(broadcaster.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            string bots = ConfigManager.Current?.BotUsername ?? string.Empty;
            if (string.IsNullOrWhiteSpace(bots)) return false;
            foreach (var part in bots.Split(','))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                if (name.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── The event tap ────────────────────────────────────────────────────

        // Gift bombs also arrive as N per-recipient GiftSub events on their heels
        // (platform behavior). After a bomb that ALERTED, suppress the GiftSub family
        // for a short window so one bomb doesn't additionally flood N single-gift
        // alerts. Monotonic clock; 0 = no such bomb seen yet.
        private long _lastGiftBombTickMs;
        internal const long GiftSubSuppressAfterBombMs = 10_000;

        /// <summary>Gift-bomb window bookkeeping (extracted for unit testing): arms the
        /// window when a bomb arrives on an ENABLED Gift Bomb card, and reports whether
        /// an inbound GiftSub is that bomb's own per-recipient echo.
        /// <para><paramref name="bombAlertEnabled"/> is the entire rule. The window only
        /// exists to kill a DOUBLE-fire (one bomb alert plus its N echoes). With the
        /// Gift Bomb card off there is no bomb alert, so there is nothing to double —
        /// arming regardless would instead swallow every trailing per-recipient alert of
        /// a GIFT SUB card the streamer deliberately turned ON. Cards default to OFF
        /// (AlertEventConfig.Enabled), so "bomb card off" is the state of every untouched
        /// install, not an edge case.</para></summary>
        internal static bool TrackGiftBombWindow(AlertFamily family, ref long lastBombTickMs,
                                                 long nowMs, bool bombAlertEnabled)
        {
            if (family == AlertFamily.GiftBomb)
            {
                // 0 is the "no bomb yet" sentinel, and TickCount64 really is 0 for the
                // first tick after boot — floor the stamp so an armed window is never
                // read back as unarmed.
                if (bombAlertEnabled)
                    Interlocked.Exchange(ref lastBombTickMs, nowMs == 0 ? 1 : nowMs);
                return false;
            }
            if (family != AlertFamily.GiftSub) return false;
            long lastBomb = Interlocked.Read(ref lastBombTickMs);
            return lastBomb != 0 && nowMs - lastBomb < GiftSubSuppressAfterBombMs;
        }

        /// <summary>
        /// Called from ScriptManager.ExecuteGenericEventAsync (pre-guard region,
        /// beside the Timer/Loyalty taps) with the already-built generic event vars.
        /// Resolves family → tier → sends the chat line, optional raid shoutout and
        /// optional visual trigger. Own try/catch discipline: a failing effect never
        /// blocks the others.
        /// </summary>
        public async Task ApplyStreamEventAsync(string eventType, Dictionary<string, string> vars)
        {
            var cfg = _config;
            if (!cfg.Enabled) return;
            if (vars is null) return;
            if (!TryMapEvent(eventType, out var family, out var sizeKey)) return;

            // Bomb window before the per-family gate, so a suppressed echo never reaches
            // the effects — but armed only when the GIFT BOMB card itself alerts, since
            // that is the only case with a double-fire to prevent (see
            // TrackGiftBombWindow). ConfigFor is read live: for a bomb it is the very
            // card the gate below checks.
            if (TrackGiftBombWindow(family, ref _lastGiftBombTickMs, Environment.TickCount64,
                                    ConfigFor(AlertFamily.GiftBomb)?.Enabled == true)) return;

            var ev = ConfigFor(family);
            if (ev is null || !ev.Enabled) return;

            // A single gifted sub has no size metric in the payload; its natural
            // size is 1 so a "From size 1" base tier behaves as the UI promises.
            int size = family == AlertFamily.GiftSub ? 1 : ParseSize(vars, sizeKey);
            var tier = PickTier(ev.Tiers, size);

            string user = ResolveActingUser(vars);
            bool firedAnything = false;
            // Per-effect outcome, for the activity row only — three independent try/catches
            // already make "the chat line went out but the visual threw" a distinguishable
            // fact, and it is the fact a streamer needs when half an alert stops working.
            // Nothing below reads these to decide anything.
            bool chatFailed = false, shoutoutFailed = false, visualFailed = false;

            // 1. Chat line
            if (tier is not null && !string.IsNullOrWhiteSpace(tier.Message))
            {
                string resolved = ResolveTokens(tier.Message, vars, size);
                string platform = vars.TryGetValue("user.platform", out var p) && !string.IsNullOrWhiteSpace(p)
                    ? p : ChatPlatforms.Twitch;
                var send = SendChat;
                if (send is not null && !string.IsNullOrWhiteSpace(resolved))
                {
                    try { await send(platform, resolved).ConfigureAwait(false); firedAnything = true; }
                    catch (Exception ex) { chatFailed = true; GlobalLogger.Error("AlertsService", "alert chat send failed", ex); }
                }
            }

            // 2. Raid auto-shoutout (Twitch feature; the raid event itself is Twitch-only).
            //    Deliberately independent of a tier match — the checkbox is a standalone
            //    toggle, not a tier effect. The channel's OWN accounts are excluded so a
            //    self-raid or a bot-account raid never shouts the channel out to itself.
            if (family == AlertFamily.Raid && ev.AutoShoutout
                && !string.IsNullOrWhiteSpace(user) && !IsOwnAccount(user))
            {
                var so = Shoutout;
                if (so is not null)
                {
                    try { await so(user).ConfigureAwait(false); firedAnything = true; }
                    catch (Exception ex) { shoutoutFailed = true; GlobalLogger.Error("AlertsService", "alert shoutout failed", ex); }
                }
            }

            // 3. Visual trigger — rides the shared fan-out; the widget graph receives
            //    Args1=KIND, Args2=USER, Args3=AMOUNT (the classic alerts-widget contract).
            if (tier is not null && !string.IsNullOrWhiteSpace(tier.LayerId) && !string.IsNullOrWhiteSpace(tier.TriggerName))
            {
                var fire = FireVisual;
                if (fire is not null)
                {
                    var eventData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Args1"] = KindLabel(family),
                        ["Args2"] = user,
                        ["Args3"] = size.ToString(CultureInfo.InvariantCulture),
                    };
                    // Trim — the picker offers registered ids, but both fields accept
                    // free text and a padded id would fail the registry lookup.
                    try { await fire(tier.LayerId.Trim(), tier.TriggerName.Trim(), eventData).ConfigureAwait(false); firedAnything = true; }
                    catch (Exception ex) { visualFailed = true; GlobalLogger.Error("AlertsService", "alert visual failed", ex); }
                }
            }

            if (firedAnything)
            {
                Interlocked.Increment(ref _alertsFiredEver);
                RecordFired(family, user, size, chatFailed, shoutoutFailed, visualFailed);
                RaiseRuntimeChanged();
            }
        }

        // ── Panel activity feed ─────────────────────────────────────────────
        /// <summary>The key this tool's rows carry in <see cref="ToolActivityRing"/>.</summary>
        public const string ActivityTool = "Alerts";

        // A display name arrives from the platform payload and nothing upstream bounds it.
        private const int ActivityNameMaxChars = 40;

        /// <summary>The row one fired alert leaves behind. <c>FIRE</c> when every effect the
        /// tier asked for ran; <c>PART</c> when at least one of the three threw, with the
        /// failed ones named — that is the case the three separate catches exist to make
        /// visible, and the one a streamer cannot otherwise see.
        /// <para>Pure so the wording is testable; the caller supplies the outcomes.</para></summary>
        internal static (string Kind, string Message) DescribeFire(
            AlertFamily family, string user, int size, bool chatFailed, bool shoutoutFailed, bool visualFailed)
        {
            string who = ClipForActivity(user);
            string subject = who.Length > 0 ? $"{KindLabel(family)} · {who}" : KindLabel(family);
            if (size > 0) subject += $" ({size.ToString(CultureInfo.InvariantCulture)})";

            if (!chatFailed && !shoutoutFailed && !visualFailed)
                return ("FIRE", $"{subject} — alert fired.");

            var failed = new List<string>(3);
            if (chatFailed) failed.Add("chat line");
            if (shoutoutFailed) failed.Add("shoutout");
            if (visualFailed) failed.Add("visual");
            return ("PART", $"{subject} — fired, but the {string.Join(" and ", failed)} failed.");
        }

        private static string ClipForActivity(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= ActivityNameMaxChars ? t : t[..ActivityNameMaxChars].TrimEnd() + "...";
        }

        // Observation only: this runs inside the event tap, so a fault here must never
        // become a fault in the alert.
        private static void RecordFired(AlertFamily family, string user, int size,
                                        bool chatFailed, bool shoutoutFailed, bool visualFailed)
        {
            try
            {
                var (kind, message) = DescribeFire(family, user, size, chatFailed, shoutoutFailed, visualFailed);
                ToolActivityRing.Record(ActivityTool, kind, message);
            }
            catch (Exception ex) { GlobalLogger.Error("AlertsService", "activity record failed", ex); }
        }

        // ── Status pill ─────────────────────────────────────────────────────
        /// <summary>
        /// What the strip's status pill says. Two of the states are things the master
        /// switch cannot say: that the tool is on with every event card still OFF (which is
        /// the state of a fresh install, because cards default off), and that Streamer.bot
        /// is down — which silently costs every chat line and every raid shoutout while the
        /// panel otherwise looks healthy.
        /// </summary>
        public enum AlertsPillState
        {
            /// <summary>The tool is switched off.</summary>
            Dormant,
            /// <summary>On, but not one event family is enabled — nothing can fire.</summary>
            NoCardsArmed,
            /// <summary>On and armed, but Streamer.bot is disconnected: the chat lines and
            /// the raid shoutout both go through it.</summary>
            Degraded,
            /// <summary>On, armed, connected.</summary>
            Armed,
        }

        /// <summary>Pure state machine behind <see cref="PillState"/>.</summary>
        internal static AlertsPillState ComputePillState(bool enabled, int enabledFamilyCount, bool connected)
        {
            if (!enabled) return AlertsPillState.Dormant;
            if (enabledFamilyCount == 0) return AlertsPillState.NoCardsArmed;
            if (!connected) return AlertsPillState.Degraded;
            return AlertsPillState.Armed;
        }

        /// <summary>The live pill state.</summary>
        public AlertsPillState PillState
            => ComputePillState(_config.Enabled, EnabledFamilyCount, WS.Instance.IsConnected);

        /// <summary>How many of the ten event families are switched on. Cards default OFF
        /// (<see cref="AlertEventConfig"/>), so a fresh install reads 0.</summary>
        public int EnabledFamilyCount
        {
            get
            {
                int n = 0;
                foreach (AlertFamily f in Enum.GetValues<AlertFamily>())
                    if (ConfigFor(f).Enabled) n++;
                return n;
            }
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public async Task InitializeAsync()
        {
            try
            {
                string? json = await _db.LoadAlertsConfigAsync().ConfigureAwait(false);
                _config = string.IsNullOrWhiteSpace(json)
                    ? new AlertsConfig()
                    : (JsonSerializer.Deserialize<AlertsConfig>(json!, JsonOpts) ?? new AlertsConfig());
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AlertsService", "config load failed", ex);
                _config = new AlertsConfig();
            }
            RaiseConfigChanged();
        }

        /// <summary>Swaps in a new config (deep-owned by the caller), persists, and
        /// notifies. The UI's single write path.</summary>
        public async Task UpdateConfigAsync(AlertsConfig newConfig)
        {
            if (newConfig is null) return;
            _config = newConfig;
            try
            {
                string json = JsonSerializer.Serialize(newConfig, JsonOpts);
                await _db.SaveAlertsConfigAsync(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("AlertsService", "config save failed", ex);
            }
            RaiseConfigChanged();
        }
    }
}
