using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Shared hash utility functions for in-memory and page-backed hash map implementations.
/// Hash functions extracted from HashMap; meta/bucket helpers extracted from PagedHashMapBase.
/// </summary>
internal static unsafe class HashUtils
{
    // ═══════════════════════════════════════════════════════════════════════
    // Hash functions — JIT-specialized by sizeof(TKey)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the hash of a key. JIT eliminates dead branches based on <c>sizeof(TKey)</c>:
    /// 4, 8 and 16 bytes each take a widening Fibonacci multiply followed by a fold and two avalanche shifts; other sizes take xxHash32 over the bytes.
    /// </summary>
    /// <remarks>
    /// Every consumer of this method masks the LOW bits of the result to pick a bucket. <see cref="FastHash64"/> documents what that requires of the hash and
    /// what happens when it is not met (#791) — read it before changing any of these.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ComputeHash<TKey>(TKey key) where TKey : unmanaged
    {
        if (sizeof(TKey) == 4)
        {
            return FastHash32(Unsafe.As<TKey, uint>(ref key));
        }

        if (sizeof(TKey) == 8)
        {
            return FastHash64(Unsafe.As<TKey, ulong>(ref key));
        }

        if (sizeof(TKey) == 16)
        {
            return FastHash128((byte*)Unsafe.AsPointer(ref key));
        }

        return XxHash32_Bytes((byte*)Unsafe.AsPointer(ref key), sizeof(TKey));
    }

    /// <summary>Wang/Jenkins integer hash — deterministic, excellent distribution, ~3-4 cycles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint WangJenkins32(uint h)
    {
        h = (h ^ 61) ^ (h >> 16);
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Multiplier for the widening Fibonacci step — the 64-bit golden-ratio constant (odd, so the multiply is a bijection).</summary>
    private const ulong Fibonacci64 = 0x9E3779B97F4A7C15UL;

    /// <summary>Second and third multipliers of the splitmix64 finalizer. Odd, so each step stays a bijection.</summary>
    private const ulong Mix64A = 0xBF58476D1CE4E5B9UL;
    private const ulong Mix64B = 0x94D049BB133111EBUL;

    /// <summary>
    /// The splitmix64 finalizer, truncated to 32 bits. Every output bit depends on every input bit — which is what the consumers need and what a
    /// shift-pair-and-fold does not give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This replaced a hand-tuned <c>(hi^lo)</c> fold plus two shifts, because that shape had a provable blind spot.</b> With <c>h_i = z_i ^ z_{i+32}</c>
    /// and shifts of 15 and 7, output bit 0 is <c>h_0 ^ h_7 ^ h_15 ^ h_22</c>, which reaches input bit 54 and no further — so bits 55-63 could not influence
    /// the lowest bucket bit at all, and any key family constant below bit 55 landed only on EVEN buckets. Measured on <c>(i&lt;&lt;55) | 0x0123456789AB</c>:
    /// 9.69 mean / 72 max probes at 75 % load, against a documented claim of "no family exceeds ~2.1 mean or ~53 max". Not reachable from <c>EntityId</c>
    /// (bits 55-63 need 2^39 entities), but this is a shared utility and the next 8-byte key is not <c>EntityId</c>.
    /// </para>
    /// <para>
    /// Reasoning about which shift pair avalanches which bit is how that blind spot survived a 17-family measured sweep. splitmix64's finalizer is published,
    /// widely analysed, and passes the standard avalanche criterion; the two extra multiplies cost ~2 ns against re-deriving the argument on every edit.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint Finalize64(ulong z)
    {
        z ^= z >> 30;
        z *= Mix64A;
        z ^= z >> 27;
        z *= Mix64B;
        z ^= z >> 31;
        return (uint)z;
    }

    /// <summary>
    /// Hash for 4-byte keys: widening Fibonacci multiply then the shared finalizer. <b>~3.3 ns</b> dependency-chain latency (Zen 4, .NET 10, measured).
    /// </summary>
    /// <remarks>
    /// Shares its shape — and its rationale — with <see cref="FastHash64"/>; see that method for why the fold and the two shifts are not optional.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash32(uint key)
    {
        uint h = Finalize64(key * Fibonacci64);
        return h == 0 ? 1u : h; // sentinel: 0 means empty slot in open addressing
    }

    /// <summary>
    /// Hash for 8-byte keys: widening Fibonacci multiply then the shared finalizer. <b>~3.3 ns</b> dependency-chain latency (Zen 4, .NET 10, measured — the
    /// figure previously stated here was ~0.7 ns, which was 4.7x optimistic and is the number capacity planning would have used).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every step here exists because its absence was measured (#791).</b> The consumers — <c>HashMap</c>, <c>HashMapKV</c>, <c>ConcurrentHashMap</c>,
    /// <c>ConcurrentHashMapKV</c> — take the bucket index as <c>hash &amp; (capacity-1)</c>, the LOW bits. That single fact constrains the whole function.
    /// </para>
    /// <para>
    /// <b>Why the multiply is widening and the high half is folded down.</b> Multiplication propagates entropy upward only: <c>(h·K) mod 2^b</c> is a
    /// bijection of <c>h mod 2^b</c> alone. A 32-bit Fibonacci multiply whose product is then masked at the low end is therefore a permutation of the low bits
    /// and nothing more — for any key family with constant low bits it maps every key to one bucket. That is what this function used to do, and
    /// <c>EntityId</c> is <c>(entityKey &lt;&lt; 16) | archetypeId</c>, so all 50 000 entities of one archetype landed in 2 buckets of 131 072 and every set
    /// operation walked a 25 000-long probe chain. Multiplying into 64 bits and folding the high half down moves that upward-propagated entropy into the range
    /// the consumer actually reads.
    /// </para>
    /// <para>
    /// <b>Why the key is not XOR-folded to 32 bits first.</b> The previous shape did, and folding before mixing destroys information that no later step can
    /// recover: <c>(i &lt;&lt; 32) | i</c> folds to zero for every <c>i</c>. Measured, that family is pathological under the old hash and stays pathological
    /// under it plus any amount of post-mixing.
    /// </para>
    /// <para>
    /// <b>Why the mixing is splitmix64 and not a hand-tuned shift pair.</b> It was a shift pair — <c>(hi^lo)</c> then <c>^15</c> and <c>^7</c> — chosen by
    /// sweeping a 17-family matrix. That shape had a blind spot the sweep could not see, because no family in it isolated the top of the key: output bit 0
    /// could not depend on input bits 55-63 at all. <see cref="Finalize64"/> carries the proof and the measurement. The lesson is the general one — a mixer
    /// justified by "these families measured well" is only as good as the families someone thought to write, while a published finalizer is justified by an
    /// avalanche argument that holds for families nobody enumerated.
    /// </para>
    /// <para>
    /// <b>The trade this makes.</b> Purely sequential keys used to average 1.00 probes — a low-bit mask of a Fibonacci product is a bijection for them, which
    /// is the best case that can exist. They now average ~1.3, and the mixing costs two extra multiplies (~2 ns). In exchange the distribution guarantee no
    /// longer rests on which key families happened to be measured. A ceiling that holds for families nobody enumerated is worth more than an optimum on one,
    /// because the family that pays for that optimum is the one the engine actually stores.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash64(ulong key)
    {
        uint h = Finalize64(key * Fibonacci64);
        return h == 0 ? 1u : h;
    }

    /// <summary>
    /// Fast hash for 16-byte keys (Guid): combine both halves through the widening Fibonacci step, then avalanche.
    /// <b>~4.5 ns</b> dependency-chain latency (Zen 4, .NET 10, measured) — still well under xxHash32_Bytes over 16 bytes, and it spreads every Guid-shaped
    /// family tested at 2.46-2.56 mean probes at 75 % load, i.e. on Knuth's theoretical optimum for linear probing.
    /// </summary>
    /// <remarks>
    /// Ends in shifts rather than in a multiply for the reason spelled out on <see cref="FastHash64"/>: the consumers mask the LOW bits, and a bare trailing
    /// multiply leaves those bits a function of the low bits of its input alone (#791). The two 64-bit halves are combined multiplicatively rather than
    /// XOR-folded so that a key whose halves cancel does not collapse to a constant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint FastHash128(byte* key)
    {
        ulong* p = (ulong*)key;

        // The two halves are combined by a FOLDED WIDENING MULTIPLY, not by XOR, and that choice is the whole point of this line.
        //
        // Two shapes have been tried and measured here. The first was `p0*K ^ rotl(p1*K, 32)`: a rotate of 32 is an involution on the halves, so for p0 == p1
        // the fold gave hi(z) == lo(z), collapsed to 0, and EVERY 16-byte key with two equal halves — (id,id), (from,to) with from==to — landed on bucket 1.
        // Measured: 65 536 such keys → 1 distinct home bucket, mean 32 768 probes. The second used distinct multipliers and an odd rotate, which fixes every
        // NATURAL family (seven Guid shapes measured at 2.46-2.56 mean probes at 75 % load) but leaves the combine SEPARABLE: `f(p0) ^ g(p1)` with both
        // bijections means that for any p0 there is exactly one p1 giving a chosen hash. Measured: 32 768 constructed keys → 1 distinct hash.
        //
        // A widening multiply of the two halves is not separable — neither operand can be solved against the other to hit a target, because the fold mixes
        // the 128-bit product's halves together. The XOR of a constant into each operand keeps a zero half from annihilating the product. Same probe counts
        // as the XOR version on every natural family, ~1 ns more, and the entire "solve one half against the other" class disappears rather than becoming
        // harder to reach. Reachability was never the argument for keeping it — the cost of removing the class was one line.
        ulong high = Math.BigMul(p[0] ^ Fibonacci64, p[1] ^ Mix64A, out ulong low);
        uint h = Finalize64(high ^ low);
        return h == 0 ? 1u : h;
    }

    /// <summary>Inlined xxHash32 over 8 bytes — deterministic, excellent distribution, ~8-10 cycles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint XxHash32_8Bytes(long key)
    {
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

        uint lo = (uint)key;
        uint hi = (uint)(key >> 32);

        uint h = Prime5 + 8u;
        h += lo * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;
        h += hi * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    /// <summary>xxHash32 over arbitrary byte length — fallback for key sizes other than 4 or 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint XxHash32_Bytes(byte* input, int len)
    {
        const uint Prime1 = 2654435761u;
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

        uint h = Prime5 + (uint)len;
        byte* p = input;
        byte* end = input + len;

        // Process 4-byte blocks
        while (p + 4 <= end)
        {
            h += *(uint*)p * Prime3;
            h = ((h << 17) | (h >> 15)) * Prime4;
            p += 4;
        }

        // Process remaining bytes
        while (p < end)
        {
            h += *p * Prime5;
            h = ((h << 11) | (h >> 21)) * Prime1;
            p++;
        }

        // Avalanche
        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Meta packing / Bucket resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pack level, next, and bucketCount into a single 64-bit value.
    /// Layout: Level(bits 56-63) | Next(bits 32-55) | BucketCount(bits 0-31).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackMeta(int level, int next, int bucketCount) =>
        ((long)(level & 0xFF) << 56) | ((long)(next & 0x00FFFFFF) << 32) | (uint)bucketCount;

    /// <summary>
    /// Unpack a 64-bit packed meta into (Level, Next, BucketCount).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int Level, int Next, int BucketCount) UnpackMeta(long packed)
    {
        int level = (int)((packed >> 56) & 0xFF);
        int next = (int)((packed >> 32) & 0x00FFFFFF);
        int bucketCount = (int)(packed & 0xFFFFFFFF);
        return (level, next, bucketCount);
    }

    /// <summary>
    /// Resolve a hash to a bucket index using bitmask arithmetic (no modulo).
    /// If the bucket has already been split this round (bucket &lt; next), the finer modulus is used.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ResolveBucket(uint hash, int level, int next, int n0)
    {
        int mod = n0 << level;                        // N0 × 2^Level (always power of 2)
        int bucket = (int)(hash & (uint)(mod - 1));   // bitmask: 1 AND instruction

        if (bucket < next)
        {
            // This bucket already split this round — use finer modulus
            bucket = (int)(hash & (uint)((mod << 1) - 1));
        }

        return bucket;
    }
}
