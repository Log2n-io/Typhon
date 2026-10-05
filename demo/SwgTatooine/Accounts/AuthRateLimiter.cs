using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SwgTatooine.Accounts;

/// <summary>
/// A token bucket per key, used to rate-limit <c>/auth</c> by client address AND by account name (A2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two keys, because either one alone is a hole.</b> Limiting per address only lets a botnet try one password on a thousand
/// accounts from a thousand addresses; limiting per name only lets one address work through a name list, a few tries each.
/// Credential stuffing is exactly the first shape and password spraying the second, so an attempt has to pass both buckets.
/// </para>
/// <para>
/// <b>A token bucket, not a fixed window.</b> A fixed window lets an attacker fire the whole allowance in the last millisecond
/// of one window and again in the first of the next — double the intended rate, at the worst possible moment. A bucket refills
/// continuously, so the long-run rate is the rate and the burst is exactly <see cref="Capacity"/>.
/// </para>
/// <para>
/// <b>In-process, and that is a stated limit rather than an oversight.</b> Two server instances behind a load balancer each
/// keep their own buckets, so the effective limit is per instance. The fix is a shared store, which is the back office's
/// problem; what this must not do is pretend. For a single demo instance — which is what Phase I deploys — it is the real limit.
/// </para>
/// <para>
/// <b>Bounded, so it cannot itself be the attack.</b> An unbounded dictionary keyed on something a client chooses is a memory
/// exhaustion vector: a million distinct names from one address would be a million entries. Past
/// <see cref="MaxTrackedKeys"/> the oldest-touched entries are dropped, and dropping one only forgives an attempt — it never
/// grants one, because a fresh bucket starts full and the limit still applies from there.
/// </para>
/// </remarks>
public sealed class AuthRateLimiter
{
    /// <summary>Attempts allowed in a burst before the refill rate governs.</summary>
    public const int Capacity = 10;

    /// <summary>How long one attempt takes to come back.</summary>
    /// <remarks>
    /// Six seconds, so a sustained rate of ten attempts a minute per key. A person logging in and mistyping twice never
    /// notices; a dictionary run gets 600 tries a day against one name, against a PBKDF2 hash. Chosen so the burst — ten —
    /// covers a browser retrying rather than a human persisting.
    /// </remarks>
    public static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(6);

    /// <summary>The most keys tracked before the least recently touched are evicted.</summary>
    public const int MaxTrackedKeys = 20_000;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly long _refillTicks;
    private readonly int _maxKeys;

    /// <summary>Creates a limiter on the defaults.</summary>
    public AuthRateLimiter() : this(Capacity, RefillInterval, MaxTrackedKeys)
    {
    }

    /// <summary>
    /// Creates a limiter with explicit parameters.
    /// </summary>
    /// <param name="capacity">Burst size.</param>
    /// <param name="refillInterval">Time for one attempt to come back.</param>
    /// <param name="maxTrackedKeys">Eviction threshold.</param>
    /// <remarks>
    /// Explicit rather than defaulted <b>so a test can state the rate it is testing</b> instead of waiting six seconds per
    /// attempt. The production path uses the parameterless constructor, so the defaults have exactly one home.
    /// </remarks>
    public AuthRateLimiter(int capacity, TimeSpan refillInterval, int maxTrackedKeys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refillInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _capacity = capacity;
        _refillTicks = (long)(refillInterval.TotalSeconds * Stopwatch.Frequency);
        _maxKeys = maxTrackedKeys;
    }

    /// <summary>
    /// Takes one attempt from every key's bucket, or refuses.
    /// </summary>
    /// <param name="keys">The keys this attempt is counted against — the client address and the account name.</param>
    /// <returns><see langword="true"/> when the attempt may proceed.</returns>
    /// <remarks>
    /// <b>All-or-nothing: if any bucket is empty, none is charged.</b> Charging the address for an attempt the name's bucket
    /// refused would let one exhausted name burn an address's whole allowance, which is a denial of service against everybody
    /// behind that address.
    /// </remarks>
    public bool TryTake(params ReadOnlySpan<string> keys)
    {
        // Peek first, charge second. Two passes over at most two keys, which is cheaper than being wrong.
        var now = Stopwatch.GetTimestamp();
        foreach (var key in keys)
        {
            if (key != null && !Bucketed(key).HasToken(now, _capacity, _refillTicks))
            {
                return false;
            }
        }

        foreach (var key in keys)
        {
            if (key != null)
            {
                Bucketed(key).Take(now, _capacity, _refillTicks);
            }
        }

        return true;
    }

    /// <summary>How many keys are currently tracked. For a test and for the stats endpoint; not a decision input.</summary>
    public int TrackedKeys => _buckets.Count;

    /// <summary>Forgets every bucket. For a fixture that must not inherit another's refusals.</summary>
    public void Reset() => _buckets.Clear();

    /// <summary>Finds or makes a key's bucket, evicting first if the table is at its bound.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its bucket.</returns>
    private Bucket Bucketed(string key)
    {
        if (_buckets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_buckets.Count >= _maxKeys)
        {
            Evict();
        }

        return _buckets.GetOrAdd(key, _ => new Bucket(_capacity));
    }

    /// <summary>
    /// Drops the least recently touched tenth of the table.
    /// </summary>
    /// <remarks>
    /// A tenth rather than one entry, so the scan's cost is amortised instead of being paid on every insert once the table is
    /// full. Nothing here is exact — a concurrent touch can make an entry look older than it is — and it does not need to be:
    /// an evicted key gets a full bucket and is limited again from there.
    /// </remarks>
    private void Evict()
    {
        var snapshot = _buckets.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.LastTouched.CompareTo(b.Value.LastTouched));
        var drop = Math.Max(1, snapshot.Length / 10);
        for (var i = 0; i < drop && i < snapshot.Length; i++)
        {
            _buckets.TryRemove(snapshot[i].Key, out _);
        }
    }

    /// <summary>One key's allowance: a token count and the timestamp it was last refilled from.</summary>
    /// <remarks>
    /// A class rather than a struct because it is mutated in place under its own lock while living in a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>, where a struct value would be replaced rather than updated and two
    /// concurrent attempts could both read a full bucket.
    /// </remarks>
    private sealed class Bucket
    {
        private readonly object _gate = new();
        private double _tokens;
        private long _stamp;

        internal Bucket(int capacity)
        {
            _tokens = capacity;
            _stamp = Stopwatch.GetTimestamp();
        }

        /// <summary>When this bucket was last read or charged, for eviction order.</summary>
        internal long LastTouched => System.Threading.Volatile.Read(ref _stamp);

        /// <summary>Whether a token is available now, refilling first.</summary>
        /// <param name="now">The caller's timestamp, so every key in one attempt is judged at one instant.</param>
        /// <param name="capacity">The ceiling.</param>
        /// <param name="refillTicks">Stopwatch ticks per token.</param>
        /// <returns><see langword="true"/> when at least one token is in hand.</returns>
        internal bool HasToken(long now, int capacity, long refillTicks)
        {
            lock (_gate)
            {
                Refill(now, capacity, refillTicks);
                return _tokens >= 1d;
            }
        }

        /// <summary>Spends one token, never going below zero.</summary>
        /// <param name="now">The caller's timestamp.</param>
        /// <param name="capacity">The ceiling.</param>
        /// <param name="refillTicks">Stopwatch ticks per token.</param>
        internal void Take(long now, int capacity, long refillTicks)
        {
            lock (_gate)
            {
                Refill(now, capacity, refillTicks);
                _tokens = Math.Max(0d, _tokens - 1d);
            }
        }

        /// <summary>Adds whatever time has earned since the last touch. Caller holds the lock.</summary>
        /// <param name="now">The caller's timestamp.</param>
        /// <param name="capacity">The ceiling.</param>
        /// <param name="refillTicks">Stopwatch ticks per token.</param>
        private void Refill(long now, int capacity, long refillTicks)
        {
            var elapsed = now - _stamp;
            if (elapsed > 0)
            {
                _tokens = Math.Min(capacity, _tokens + ((double)elapsed / refillTicks));
            }

            System.Threading.Volatile.Write(ref _stamp, now);
        }
    }
}
