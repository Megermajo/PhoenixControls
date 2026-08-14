using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Scheduling (recurring timed chat messages) — the Hub-side wiring of
    // the Scheduling pre-build tool (sibling of ScriptManager.CustomCommands.cs).
    //
    // Like CustomCommands it registers NO imperative script command and NO built-in chat
    // provider: the tool never RESPONDS to chat, it only POSTS on a timer, and the same
    // result is hand-buildable in Architect (Schedule.Recurring node → Chat.Send). This
    // wires only the SchedulingService.ChatSend seam — the service can't reach Hub state
    // (broadcaster name / uptime) or the send path itself, so token resolution + the send
    // live here. Chat-line counting for the MinChatLines gate is fed separately from
    // ExecuteOnChatScriptsAsync (SchedulingService.NoteChatActivity).
    public partial class ScriptManager
    {
        private void RegisterSchedulingCommands()
        {
            var svc = SchedulingService.Instance;

            // Seam: post one resolved line to the streamer's main (Twitch) chat. Token
            // resolution runs here where Hub state is reachable; SchedulingService stays
            // Hub-state-agnostic. Reuses the exact SendTwitchChatCore path chat.send /
            // *.send_chat use (500-char cap, connectivity guard, fire-and-forget) — no new
            // send path. Multi-platform posting is a trivial future extension (switch on a
            // per-schedule platform like SendCustomCommandsReply); v1 posts to Twitch main.
            svc.ChatSend = (message, fireCount) =>
                Task.FromResult(TrySendSchedulingLine(ResolveSchedulingTokens(message, fireCount)));
        }

        /// <summary>
        /// Posts one scheduled line and reports whether it reached the send path.
        /// SendTwitchChatCore's own drops (blank chat action, dropped link, over the
        /// 500-char cap) log and <c>return</c> — they never throw — so the tick has no way
        /// to tell a post from a no-op unless the same conditions are checked HERE and
        /// surfaced. Without that, the common "Chat Action name never set" misconfiguration
        /// let every schedule count a fire per interval while chat stayed silent.
        /// </summary>
        private SchedulingSendResult TrySendSchedulingLine(string resolved)
        {
            if (string.IsNullOrWhiteSpace(resolved))
                return SchedulingSendResult.Failed("the message resolved to an empty line (check its tokens).");
            if (resolved.Length > 500)
                return SchedulingSendResult.Failed(
                    $"it is {resolved.Length} characters and Twitch caps a chat message at 500.");
            if ((ConfigManager.Current?.StreamerBotChatAction ?? "").Trim().Length == 0)
                return SchedulingSendResult.Failed(
                    "the Streamer.bot Chat Action name is not configured (Hub Settings → Connection → Chat Action Name).");
            if (!WS.Instance.IsConnected)
                return SchedulingSendResult.Failed("the Streamer.bot WebSocket is not connected.");

            SendTwitchChatCore(resolved, "scheduling");
            return SchedulingSendResult.Ok();
        }

        // Every supported token, matched in ONE pass. A value substituted into the string is
        // never re-scanned, so a stream title / channel name that happens to contain "{random}"
        // (or any other token) survives verbatim instead of being expanded — the multi-pass
        // shape this replaced expanded it, because the {random} sweep ran over the whole
        // already-substituted string. Same idiom as CustomCommandsService.RenderAsync.
        private static readonly Regex SchedulingToken =
            new(@"\{([^{}]+)\}", RegexOptions.Compiled);

        // Resolve the supported message tokens for a recurring post. There is no triggering
        // user, so {user}/{args} do not apply.
        internal string ResolveSchedulingTokens(string message, int fireCount)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";
            if (message.IndexOf('{') < 0) return message;   // no tokens at all — skip the regex

            // {game} / {title} read the ambient stream-info cache (StreamUpdate/ChannelUpdate
            // events + setter/Get-User write-through + a lazy seed). Kick the seed only when
            // referenced; the tracker-empty check inside EnsureStreamInfoSeeded keeps it a
            // no-op once warm. The values read "" until the cache learns them.
            if (message.IndexOf("{game}", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("{title}", StringComparison.OrdinalIgnoreCase) >= 0)
                EnsureStreamInfoSeeded();

            // ONE lock acquisition for both halves: reading CurrentTitle and CurrentGame
            // separately lets a StreamUpdate land between them, so the posted line could
            // pair a fresh title with the previous category (or the reverse).
            var (title, game) = StreamInfoTracker.CurrentPair;

            return RenderSchedulingTokens(
                message, fireCount,
                ConfigManager.Current?.BroadcasterUsername ?? "",   // {channel}, live-read so a Settings edit shows
                ResolveBroadcasterUptime(),                         // {uptime}, "" while offline
                game,
                title);
        }

        /// <summary>
        /// The pure render half of <see cref="ResolveSchedulingTokens"/> (extracted for unit
        /// testing — all Hub state arrives as arguments). Resolves {count} / {channel} /
        /// {uptime} / {game} / {title} / {random}[.N|.A-B] in a single pass; an unknown token
        /// is left verbatim, and a substituted value is never re-scanned for tokens.
        /// </summary>
        internal static string RenderSchedulingTokens(
            string message, int fireCount, string channel, string uptime, string game, string title)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";

            return SchedulingToken.Replace(message, m =>
            {
                string key = m.Groups[1].Value.Trim().ToLowerInvariant();
                switch (key)
                {
                    case "count":   return fireCount.ToString(CultureInfo.InvariantCulture);
                    case "channel": return channel ?? "";
                    case "uptime":  return uptime ?? "";
                    case "game":    return game ?? "";
                    case "title":   return title ?? "";
                    case "random":  return RandomInRange(1, 100).ToString(CultureInfo.InvariantCulture);
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

                return m.Value;   // unknown token — leave verbatim
            });
        }

        // Inclusive [lo, hi], bounds tolerated in either order. Random.Shared is thread-safe;
        // its upper bound is exclusive, so +1 — widened to long first so hi = int.MaxValue
        // can't overflow the addition.
        private static int RandomInRange(int lo, int hi)
        {
            if (hi < lo) (lo, hi) = (hi, lo);
            long exclusiveHi = (long)hi + 1;
            return Random.Shared.Next(lo, (int)Math.Min(exclusiveHi, int.MaxValue));
        }
    }
}
