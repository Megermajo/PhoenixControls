using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Phoenix.Controls.ViewerServer.Internal;

/// <summary>
/// Per-IP throttle for the pairing flow on a LAN-exposed ViewerServer. With
/// no throttle, an attacker on the same network could mint unlimited pairing
/// handles via <c>POST /v/api/pair/begin</c> and fire ~10⁶ guesses against
/// <c>POST /v/api/pair/complete</c> in minutes, hitting the 6-digit PIN
/// before its TTL expires.
///
/// <para>Two windows, both sliding 60s:</para>
/// <list type="bullet">
///   <item>
///     <see cref="TryRegisterBegin"/> caps <c>/pair/begin</c> calls per IP
///     to <see cref="MaxBeginsPerMinute"/>. The pending list is bounded
///     additionally by <see cref="PinManager.MaxPending"/>.
///   </item>
///   <item>
///     <see cref="TryRegisterCompleteAttempt"/> caps wrong-PIN guesses per
///     IP to <see cref="MaxFailuresPerMinute"/>. Exceeding that puts the IP
///     in a <see cref="LockoutDuration"/> denial state for both endpoints.
///     A successful claim (<see cref="ClearOnSuccess"/>) resets that IP's
///     fail counter.
///   </item>
/// </list>
///
/// <para>Global memory bound: <see cref="MaxTrackedIps"/> entries. When the
/// limiter is at capacity and sees a new IP, the oldest-touched entry is
/// evicted. This stops an IP-spraying attacker from exhausting Hub memory.
/// </para>
/// </summary>
internal sealed class PairingRateLimiter
{
    internal const int MaxBeginsPerMinute    = 5;
    internal const int MaxFailuresPerMinute  = 10;
    internal const int MaxTrackedIps         = 1024;
    internal static readonly TimeSpan Window         = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private sealed class IpState
    {
        public readonly object Gate = new();
        public readonly Queue<DateTimeOffset> Begins   = new();
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset LockoutUntil = DateTimeOffset.MinValue;
        public DateTimeOffset LastTouched  = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, IpState> _ips = new();

    /// <summary>
    /// Allows or denies <c>/pair/begin</c> for <paramref name="ip"/>.
    /// Returns <c>false</c> when the IP is locked out (from prior brute-force
    /// attempts on /complete) or has already burned its 5 begins this minute.
    /// </summary>
    public bool TryRegisterBegin(IPAddress? ip)
    {
        var state = Get(ip);
        if (state == null) return true; // unknown remote — let it through
        lock (state.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (state.LockoutUntil > now) return false;
            Prune(state.Begins, now);
            if (state.Begins.Count >= MaxBeginsPerMinute) return false;
            state.Begins.Enqueue(now);
            state.LastTouched = now;
            return true;
        }
    }

    /// <summary>
    /// Records a <c>/pair/complete</c> attempt and returns whether it should
    /// be processed. Pass <paramref name="wasFailure"/> = true AFTER the PIN
    /// check has rejected the guess; this trips the lockout once the IP's
    /// failures-this-minute crosses <see cref="MaxFailuresPerMinute"/>.
    ///
    /// <para>The return value is the gate decision for the NEXT request from
    /// this IP — callers always process the current request; this is the
    /// "should I bother checking the PIN at all" signal for the boundary.</para>
    /// </summary>
    public bool TryRegisterCompleteAttempt(IPAddress? ip)
    {
        var state = Get(ip);
        if (state == null) return true;
        lock (state.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (state.LockoutUntil > now) return false;
            state.LastTouched = now;
            return true;
        }
    }

    /// <summary>
    /// Records a failed PIN guess from <paramref name="ip"/>. After
    /// <see cref="MaxFailuresPerMinute"/> failures inside the sliding
    /// window, the IP enters <see cref="LockoutDuration"/>-long lockout
    /// for both endpoints.
    /// </summary>
    public void NoteCompleteFailure(IPAddress? ip)
    {
        var state = Get(ip);
        if (state == null) return;
        lock (state.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(state.Failures, now);
            state.Failures.Enqueue(now);
            if (state.Failures.Count >= MaxFailuresPerMinute)
            {
                state.LockoutUntil = now + LockoutDuration;
                state.Failures.Clear();
            }
            state.LastTouched = now;
        }
    }

    /// <summary>
    /// Resets <paramref name="ip"/>'s failure counter and any active lockout
    /// after a successful claim. Called from the pair-complete success path.
    /// </summary>
    public void ClearOnSuccess(IPAddress? ip)
    {
        var state = Get(ip);
        if (state == null) return;
        lock (state.Gate)
        {
            state.Failures.Clear();
            state.LockoutUntil = DateTimeOffset.MinValue;
            state.LastTouched = DateTimeOffset.UtcNow;
        }
    }

    private IpState? Get(IPAddress? ip)
    {
        if (ip is null) return null;
        string key = ip.ToString();
        var state = _ips.GetOrAdd(key, _ => new IpState());

        // Bound the map: when over capacity, evict the least-recently-touched
        // entry. Eviction is best-effort under contention — the worst case
        // is a single extra LRU pass next call.
        if (_ips.Count > MaxTrackedIps)
        {
            string? victim = null;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;
            foreach (var kv in _ips.ToArray())
            {
                if (kv.Key == key) continue;
                if (kv.Value.LastTouched < oldest)
                {
                    oldest = kv.Value.LastTouched;
                    victim = kv.Key;
                }
            }
            if (victim != null) _ips.TryRemove(victim, out _);
        }

        return state;
    }

    private static void Prune(Queue<DateTimeOffset> window, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (window.Count > 0 && window.Peek() <= cutoff) window.Dequeue();
    }
}
