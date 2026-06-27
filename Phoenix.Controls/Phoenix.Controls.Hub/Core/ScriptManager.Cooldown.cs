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
    // cooldown.check(user, globalCd, userCd) — Ready/Blocked gate. Rolls the
    // next-available time forward by max(globalCd, userCd) seconds on a
    // Ready return so a cluster of Ready callers in the same handler don't
    // all pass. Accepts a "__nodecd::<id>::<user>" alias key (C2b) to
    // namespace per-node cooldowns separately from per-user ones.
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
            // cooldown.check(user, globalCd, userCd)
            //   Returns "true" when the per-user cooldown has elapsed (Ready branch),
            //   "false" while still cooling down (Blocked branch). On a Ready return,
            //   the next-available time is rolled forward by max(globalCd, userCd) seconds
            //   so a cluster of Ready callers in the same handler don't all pass.
            //   First arg may be a "__nodecd::<id>::<user>" alias key — see C2b.
            _engine.RegisterCommand("cooldown.check", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string keyHint = bound?.GetOrDefault<string>("User", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                if (string.IsNullOrEmpty(keyHint)) return "false";
                string key = keyHint.StartsWith("__nodecd::", StringComparison.Ordinal)
                    ? keyHint
                    : $"cd::{keyHint}";

                int globalCd = (bound != null && bound.ContainsKey("GlobalCooldownMs"))
                    ? bound.Get<int>("GlobalCooldownMs")
                    : (int.TryParse(ArgOrEmpty(args, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0);
                int userCd   = (bound != null && bound.ContainsKey("UserCooldownMs"))
                    ? bound.Get<int>("UserCooldownMs")
                    : (int.TryParse(ArgOrEmpty(args, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0);
                int waitSec = Math.Max(0, Math.Max(globalCd, userCd));

                long nowTicks = DateTime.UtcNow.Ticks;

                // [P1 swarm-audit 2026-05-29] The TryGetValue read + conditional roll-
                // forward write was a TOCTOU race: two Ready callers sharing a key could
                // both observe ready==true and both pass before either armed the next
                // expiry. Serialise the check + arm under the same per-key RMW lock that
                // queue.push / db.increment use so the gate is atomic per key.
                var rmwLock = GetRmwLock("cooldown:" + key);
                await rmwLock.WaitAsync().ConfigureAwait(false);
                bool ready;
                try
                {
                    long expiry = _cooldownExpiryUtc.TryGetValue(key, out var t) ? t : 0;
                    ready = nowTicks >= expiry;
                    if (ready && waitSec > 0)
                        _cooldownExpiryUtc[key] = nowTicks + TimeSpan.FromSeconds(waitSec).Ticks;
                }
                finally
                {
                    try { rmwLock.Release(); } catch (ObjectDisposedException) { }
                }

                return ready ? "true" : "false";
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
