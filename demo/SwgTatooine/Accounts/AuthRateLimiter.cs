using System;
using System.Collections.Generic;
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
/// <b>ONE lock for the whole limiter, and the first version's cleverness was a bug.</b> That version gave each bucket its own
/// lock and took an attempt in two passes — peek every key, then charge every key — so that a refusal on one key charged
/// nothing. Between the two passes other threads peeked too: with a capacity of 10 and 500 concurrent attempts it granted 25,
/// which its own test caught. Check-then-act is not made atomic by locking the parts. The whole attempt is now one critical
/// section, which is also what makes the all-or-nothing property true rather than merely intended.
/// </para>
/// <para>
/// <b>The contention does not matter here, and claiming otherwise is what caused the bug.</b> Every caller of this is about to
/// spend a few hundred microseconds in PBKDF2. A global lock held for two dictionary lookups and two floating-point updates is
/// three or four orders of magnitude below that, and there is no version of this endpoint where the limiter is the bottleneck.
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

    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
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
    /// Takes one attempt from every key's bucket, or refuses and charges nothing.
    /// </summary>
    /// <param name="keys">
    /// The keys this attempt is counted against — the client address and the account name. <see langword="null"/> entries are ignored.
    /// </param>
    /// <returns><see langword="true"/> when the attempt may proceed.</returns>
    /// <remarks>
    /// <b>All-or-nothing: if any bucket is empty, none is charged.</b> Charging the address for an attempt the name's bucket
    /// refused would let one exhausted name burn an address's whole allowance, which is a denial of service against everybody
    /// behind that address. The whole decision happens under one lock, so the refill, the test and the charge are a single
    /// atomic step and no concurrent attempt can observe the gap — which is where the first version granted two and a half
    /// times its capacity.
    /// </remarks>
    public bool TryTake(params ReadOnlySpan<string> keys)
    {
        // One timestamp for the whole attempt, so each key is judged at the same instant and two keys cannot refill by
        // different amounts within one call.
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            // Resolve and refill every key first. Refilling is not a charge, so doing it for a key whose sibling turns out to
            // be empty costs nothing but the clock reading it already deserved.
            foreach (var key in keys)
            {
                if (key == null)
                {
                    continue;
                }

                // A null bucket is a table with no room and nothing safe to evict — see Bucketed. Refused, like an empty one.
                var bucket = Bucketed(key, now);
                if (bucket == null || !bucket.Refill(now, _capacity, _refillTicks).HasToken)
                {
                    return false;
                }
            }

            foreach (var key in keys)
            {
                if (key != null)
                {
                    _buckets[key].Spend();
                }
            }

            return true;
        }
    }

    /// <summary>How many keys are currently tracked. For a test and for the stats endpoint; not a decision input.</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Forgets every bucket. For a fixture that must not inherit another's refusals.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _buckets.Clear();
        }
    }

    /// <summary>Finds or makes a key's bucket, evicting first if the table is at its bound. Caller holds the lock.</summary>
    /// <param name="key">The key.</param>
    /// <param name="now">The attempt's timestamp, which a fresh bucket starts from.</param>
    /// <returns>Its bucket.</returns>
    private Bucket Bucketed(string key, long now)
    {
        if (_buckets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_buckets.Count >= _maxKeys && !Evict())
        {
            // The table is full of buckets that all still owe something, so there is no room for this key and no safe way to make
            // room. Refused rather than admitted: a limiter that cannot track a key must not pretend the key has an allowance.
            return null;
        }

        var fresh = new Bucket(_capacity, now);
        _buckets[key] = fresh;
        return fresh;
    }

    /// <summary>
    /// Drops up to a tenth of the table, taking only buckets that owe nothing. Caller holds the lock.
    /// </summary>
    /// <returns>Whether anything was dropped, and so whether there is room for another key.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only a FULL bucket may be evicted, and the previous rule — least recently touched — was a way through the per-name
    /// limit.</b> A recreated bucket starts at <c>_capacity</c>, so dropping an EXHAUSTED one hands its key a fresh allowance.
    /// The name key is whatever the client sent, so the attack was direct and cheap: exhaust the victim's name, flood the table
    /// with more than <c>_maxKeys</c> other names until the victim's bucket is the oldest, let it be evicted, and the next attempt
    /// on that name starts from ten again. Repeat for unbounded guessing against one account. The class comment claimed eviction
    /// "can forgive an attempt but never grant one"; that is now true, because a bucket at full capacity is the one bucket whose
    /// removal loses no information.
    /// </para>
    /// <para>
    /// <b>No sort any more, because there is nothing to order.</b> Full buckets are interchangeable — each says only "this key has
    /// spent nothing" — so the first ones found are as good as the oldest. That turns an O(n log n) pass with two full-table
    /// allocations, run inside the single lock every authentication attempt takes, into one allocation-free scan that stops as soon
    /// as it has room.
    /// </para>
    /// </remarks>
    private bool Evict()
    {
        var target = Math.Max(1, _buckets.Count / 10);
        _evicting.Clear();
        foreach (var pair in _buckets)
        {
            if (pair.Value.OwesNothing(_capacity))
            {
                _evicting.Add(pair.Key);
                if (_evicting.Count >= target)
                {
                    break;
                }
            }
        }

        for (var i = 0; i < _evicting.Count; i++)
        {
            _buckets.Remove(_evicting[i]);
        }

        return _evicting.Count > 0;
    }

    /// <summary>Scratch for <see cref="Evict"/>: the keys to drop, collected before the dictionary is written. Lock-held.</summary>
    private readonly List<string> _evicting = [];

    /// <summary>
    /// One key's allowance: a token count and the timestamp it was last refilled from.
    /// </summary>
    /// <remarks>
    /// <b>No lock of its own, deliberately.</b> Every method here is called with the limiter's single lock held, which is the
    /// only thing that makes an attempt atomic across two keys. Giving a bucket its own lock is what the first version did, and
    /// it made each step atomic while leaving the decision as a whole racy.
    /// </remarks>
    private sealed class Bucket
    {
        private double _tokens;
        private long _stamp;

        internal Bucket(int capacity, long now)
        {
            _tokens = capacity;
            _stamp = now;
        }

        /// <summary>When this bucket was last refilled, for eviction order.</summary>
        internal long LastTouched => _stamp;

        /// <summary>Whether at least one token is in hand, as of the last <see cref="Refill"/>.</summary>
        internal bool HasToken => _tokens >= 1d;

        /// <summary>
        /// Whether this bucket has spent nothing, so forgetting it would forgive nothing.
        /// </summary>
        /// <param name="capacity">The limiter's capacity, which a bucket that owes nothing still holds in full.</param>
        /// <returns>Whether it is safe to evict.</returns>
        /// <remarks>
        /// Read without refilling on purpose: a bucket that has spent something and would refill to full by now is still a bucket
        /// whose key has been seen recently, and keeping it costs one entry. Being conservative here is the safe direction.
        /// </remarks>
        internal bool OwesNothing(int capacity) => _tokens >= capacity;

        /// <summary>Adds whatever time has earned since the last touch, and returns this bucket so a caller can chain the test.</summary>
        /// <param name="now">The attempt's timestamp.</param>
        /// <param name="capacity">The ceiling.</param>
        /// <param name="refillTicks">Stopwatch ticks per token.</param>
        /// <returns>This bucket.</returns>
        internal Bucket Refill(long now, int capacity, long refillTicks)
        {
            var elapsed = now - _stamp;
            if (elapsed > 0)
            {
                _tokens = Math.Min(capacity, _tokens + ((double)elapsed / refillTicks));
                _stamp = now;
            }

            return this;
        }

        /// <summary>Spends one token. Only called after <see cref="HasToken"/> was true under the same lock.</summary>
        internal void Spend() => _tokens -= 1d;
    }
}
