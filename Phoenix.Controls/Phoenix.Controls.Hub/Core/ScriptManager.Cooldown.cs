using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: cooldown.check + time.seconds_since_last_fire.
    // Two related rate-gating handlers that both consult class-level
    // ConcurrentDictionary fields (_cooldownExpiryUtc / _lastFireUtcTicks)
    // declared in ScriptManager.cs. Lifting them into a sibling partial
    // keeps the dictionaries available because partial classes share state.
    //
    // cooldown.check(user, globalCd, userCd, nodeKey) — Ready/Blocked gate over
    // up to two timers: a channel-wide GLOBAL bucket keyed by the node (always
    // enforced, globalCd seconds) plus a per-USER bucket keyed by node+user
    // (userCd seconds, only when User is set). Ready requires every active bucket
    // to have elapsed; a 0 duration disables that bucket. Legacy 3-arg exports
    // (no nodeKey) keep the old single-bucket behavior — empty User = Blocked,
    // else one bucket for max(globalCd, userCd). Keys stored in _cooldownExpiryUtc.
    //
    // time.seconds_since_last_fire(key) — generalised "how long since X"
    // probe used by uptime-gated alerts. Returns the sentinel "999999999"
    // for first-time keys (every threshold check passes) and updates the
    // per-key timestamp atomically.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterCooldownCommands()
        {
            // cooldown.check(user, globalCd, userCd, nodeKey)
            //   Ready when every ACTIVE bucket has elapsed; Blocked while any is
            //   still cooling. New exports pass a per-node key (arg 4) → a
            //   channel-wide GLOBAL bucket is ALWAYS enforced (globalCd s) plus a
            //   per-user bucket when User is set (userCd s). A 0 duration disables
            //   that bucket. Legacy 3-arg exports (no nodeKey) keep the prior
            //   behavior: empty User = Blocked, else one bucket for max(g,u).
            _engine.RegisterCommand("cooldown.check", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string user    = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string nodeKey = bound?.GetOrDefault<string>("NodeKey", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);

                int globalCd = (bound != null && bound.ContainsKey("GlobalCooldownMs"))
                    ? bound.Get<int>("GlobalCooldownMs")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0);
                int userCd   = (bound != null && bound.ContainsKey("UserCooldownMs"))
                    ? bound.Get<int>("UserCooldownMs")
                    : (int.TryParse(ArgOrEmpty(args, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0);

                // Resolve the (up to two) buckets this call gates on plus the single
                // lock that serialises their check+arm.
                (string key, int waitSec)? globalBucket = null;
                (string key, int waitSec)? userBucket   = null;
                string lockKey;
                if (!string.IsNullOrEmpty(nodeKey))
                {
                    string baseKey = nodeKey.StartsWith("__nodecd::", StringComparison.Ordinal)
                        ? nodeKey
                        : "__nodecd::" + nodeKey;
                    lockKey = baseKey;
                    globalBucket = (baseKey, globalCd);                        // channel-wide, always
                    if (!string.IsNullOrEmpty(user))
                        userBucket = (baseKey + "::" + user, userCd);          // per-user, when set
                }
                else
                {
                    // Legacy 3-arg export (no node id): preserve prior semantics.
                    if (string.IsNullOrEmpty(user)) return "false";           // empty User = Blocked
                    lockKey = user.StartsWith("__nodecd::", StringComparison.Ordinal) ? user : "cd::" + user;
                    globalBucket = (lockKey, Math.Max(globalCd, userCd));      // single bucket
                }

                long nowTicks = DateTime.UtcNow.Ticks;

                // Serialise the whole check + arm under one lock so two callers can't
                // both observe Ready before either arms (the TOCTOU the single-bucket
                // path already guarded), now spanning both buckets. Lock on the
                // node/legacy key: distinct nodes and distinct users of a node don't
                // false-share, but one call's two buckets always take the same lock.
                var rmwLock = GetRmwLock("cooldown:" + lockKey);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Check every active bucket BEFORE arming any — a Blocked bucket
                    // must not roll the others forward, or a blocked call would still
                    // consume the other timers.
                    bool ready = true;
                    if (globalBucket is { } gb)
                    {
                        long exp = _cooldownExpiryUtc.TryGetValue(gb.key, out var t) ? t : 0;
                        if (nowTicks < exp) ready = false;
                    }
                    if (ready && userBucket is { } ub)
                    {
                        long exp = _cooldownExpiryUtc.TryGetValue(ub.key, out var t) ? t : 0;
                        if (nowTicks < exp) ready = false;
                    }
                    if (ready)
                    {
                        if (globalBucket is { } gb2 && gb2.waitSec > 0)
                            _cooldownExpiryUtc[gb2.key] = nowTicks + TimeSpan.FromSeconds(gb2.waitSec).Ticks;
                        if (userBucket is { } ub2 && ub2.waitSec > 0)
                            _cooldownExpiryUtc[ub2.key] = nowTicks + TimeSpan.FromSeconds(ub2.waitSec).Ticks;
                    }
                    return ready ? "true" : "false";
                }
                finally
                {
                    try { rmwLock.Release(); } catch (ObjectDisposedException) { }
                }
            });

            // P2 — time.seconds_since_last_fire(key)
            //   Returns the seconds elapsed since the last call against this key, then
            //   updates the per-key timestamp atomically. First-time keys return a sentinel
            //   "infinity" — large enough that any threshold check evaluates to "ready"
            //   without overflowing int.MaxValue when downstream consumers parse it.
            //   Used by uptime-gated alerts and as a generalised cooldown beyond Flow.Cooldown.
            _engine.RegisterCommand("time.seconds_since_last_fire", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string key = bound?.GetOrDefault<string>("Key", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrWhiteSpace(key)) return "0";

                long nowTicks  = DateTime.UtcNow.Ticks;
                long lastTicks = _lastFireUtcTicks.TryGetValue(key, out var t) ? t : 0;
                _lastFireUtcTicks[key] = nowTicks;
                if (lastTicks == 0)
                    return "999999999"; // sentinel "never fired" — every threshold check passes
                long elapsedSec = (nowTicks - lastTicks) / TimeSpan.TicksPerSecond;
                if (elapsedSec < 0) elapsedSec = 0;

                await Task.CompletedTask;
                return elapsedSec.ToString(CultureInfo.InvariantCulture);
            });
        }
    }
#pragma warning restore CS1998
}
