using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Issue #791 — <see cref="HashUtils"/> feeds open-addressed maps that mask the LOW bits of the hash to pick a bucket, so the hash must spread key families
/// whose low bits carry no entropy. It did not: a Fibonacci multiply masked at the low end is a permutation of the low bits alone, and
/// <c>EntityId = (entityKey &lt;&lt; 16) | archetypeId</c> holds a constant there.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests assert probe length, not correctness.</b> The broken hash was correct — every lookup returned the right answer, at 25 000 probes each. The
/// pre-existing fixtures use <c>i</c> and <c>i * 7 + 1</c>, whose entropy is entirely in the low bits, which is precisely the family a low-bit mask of a
/// Fibonacci product handles perfectly. A distribution defect is only visible to a test that measures distribution.
/// </para>
/// <para>
/// The bound is deliberately loose (<see cref="MaxMeanProbes"/>). It is not tuned to the current function — it sits where "the map still behaves like a
/// hash map" stops being true, so a future hash change may move the numbers around inside it and is caught the moment it reintroduces a chain.
/// </para>
/// </remarks>
[TestFixture]
public class HashDistributionTests
{
    /// <summary>Linear probing at 75 % load averages ~1.8 probes for a uniform hash; 4 is well past any healthy function and far below a collapse.</summary>
    private const double MaxMeanProbes = 4.0;

    /// <summary>A single lookup should never walk a chain. 256 is ~5x the worst family measured and ~100x below a collapsed bucket.</summary>
    private const int MaxWorstProbes = 256;

    private const int KeyCount = 20_000;

    private static IEnumerable<TestCaseData> KeyFamilies()
    {
        // The family that broke: 48-bit monotonic key in the high bits, constant 16-bit archetype routing id in the low bits.
        yield return Family("EntityId, one archetype", i => ((long)i << 16) | 1);

        // A polymorphic view spans several archetypes — a few distinct low-bit values rather than one.
        yield return Family("EntityId, eight archetypes", i => ((long)i << 16) | (uint)(i % 8));

        // A churned archetype: the key space is sparse because destroyed keys are never recycled.
        yield return Family("EntityId, sparse keys", i => ((long)i * 977 << 16) | 3);

        // Aligned offsets — low bits structurally zero.
        yield return Family("64-byte aligned", i => (long)i * 64);
        yield return Family("4096-byte aligned", i => (long)i * 4096);

        // Two small fields packed into one key: both halves have low entropy, and an XOR-fold would collide them wholesale.
        yield return Family("packed pair", i => ((long)(i % 1024) << 32) | (uint)(i / 1024));

        // The XOR-fold trap: folding the two halves together yields zero for every key.
        yield return Family("halves cancel under XOR-fold", i => ((long)i << 32) | (uint)i);

        // Entropy only in the high half — the mirror image of the family that broke.
        yield return Family("high half only", i => (long)i << 32);

        // Entropy only in the TOP of the key. The previous mixer folded (hi^lo) then shifted by 15 and 7, which left output bit 0 a function of input bits
        // 0-54 alone — so a family whose entropy sits above bit 54 landed exclusively on even buckets and measured 9.69 mean / 72 max at 75 % load, while
        // every family anyone had thought to write passed. 20 000 keys need 15 bits, so bit 48 is the highest base that keeps them distinct (48+15 = 63);
        // eight of those fifteen bits sit above 54, which is enough to expose the blind spot. It is here because no sweep finds a hole it has no probe for.
        yield return Family("entropy above bit 48", i => (long)i << 48);

        // Families the OLD hash handled perfectly. They must not regress into a chain.
        yield return Family("sequential", i => i);
        yield return Family("sequential, large offset", i => 1_000_000_000L + i);
    }

    /// <summary>
    /// 16-byte keys, which <see cref="HashUtils.ComputeHash{TKey}"/> routes to <c>FastHash128</c>. Structurally separate from the 8-byte matrix because the
    /// defect this guards is about how the two halves COMBINE, which no single-word family can express.
    /// </summary>
    private static IEnumerable<TestCaseData> Guid16Families()
    {
        // The collapse: with one shared multiplier and a rotate of 32, `z = y ^ rotl(y,32)` has hi(z) == lo(z) for equal halves, the (hi^lo) fold yields 0,
        // and EVERY such key hashed to the sentinel 1. Real shapes that hit it: (id, id), (from, to) where from == to, a zeroed second field.
        yield return Guid16("equal halves", i => ((ulong)i, (ulong)i));

        // The same cancellation one step removed — halves that differ by a rotation rather than being identical.
        yield return Guid16("halves are rotations", i => ((ulong)i, BitOperations.RotateLeft((ulong)i, 32)));

        // One half constant: the shape of a composite key whose second field is rarely set.
        yield return Guid16("high half zero", i => ((ulong)i, 0UL));
        yield return Guid16("low half zero", i => (0UL, (ulong)i));

        // Both halves low-entropy and correlated.
        yield return Guid16("both halves small", i => ((ulong)(i % 512), (ulong)(i / 512)));

        // The healthy baseline — must not regress.
        yield return Guid16("independent halves", i => ((ulong)i * 0x9E3779B97F4A7C15UL, (ulong)i * 0xC2B2AE3D27D4EB4FUL));

        // The CONSTRUCTIBLE lattice. An XOR-combine of two invertible functions, `f(p0) ^ g(p1)`, is SEPARABLE: for any p0 you can solve p1 so the two terms
        // cancel exactly, and a whole family lands on one hash. This generator does that against the shape the function briefly had —
        // `(p0*Fib) ^ rotl(p1*Mix64A, 29)` — by inverting it: `p1 = rotr(p0*Fib, 29) * Mix64A⁻¹`, where the inverse is mod 2^64 (Mix64A is odd, so it exists).
        // Verified in Python: z == 0 for every constructed pair, hence one hash for the whole family.
        //
        // That shape spread every NATURAL family perfectly — seven Guid shapes at 2.46-2.56 mean probes — which is exactly why "no realistic key hits it" was
        // an argument for shipping it, and exactly why that argument is worth a regression test rather than a comment. The widening-multiply combine is not
        // separable, so this family spreads.
        yield return Guid16("constructed to cancel a separable combine",
            i => ((ulong)i, BitOperations.RotateRight((ulong)i * 0x9E3779B97F4A7C15UL, 29) * 0x96DE1B173F119089UL));
    }

    private static TestCaseData Guid16(string name, Func<int, (ulong Lo, ulong Hi)> gen)
    {
        var keys = new (ulong Lo, ulong Hi)[KeyCount];
        for (var i = 0; i < KeyCount; i++)
        {
            keys[i] = gen(i + 1);
        }
        return new TestCaseData((object)keys).SetName($"SixteenByteKeysSpreadAcrossBuckets({name})");
    }

    /// <summary>Every 16-byte family must spread, for the same reason the 8-byte ones must — the consumers mask the LOW bits of the result.</summary>
    [Test]
    [TestCaseSource(nameof(Guid16Families))]
    public void SixteenByteKeysSpreadAcrossBuckets((ulong Lo, ulong Hi)[] keys)
    {
        Assert.That(new HashSet<(ulong, ulong)>(keys).Count, Is.EqualTo(keys.Length), "PREMISE: the family generates distinct keys");

        var capacity = 1;
        while (capacity < (int)(keys.Length / 0.75))
        {
            capacity <<= 1;
        }

        var buckets = new HashSet<uint>();
        var histogram = new int[capacity];
        foreach (var key in keys)
        {
            var k = key;
            var hash = HashUtils.ComputeHash(k);
            var bucket = hash & (uint)(capacity - 1);
            buckets.Add(bucket);
            histogram[bucket]++;
        }

        var worst = 0;
        foreach (var count in histogram)
        {
            if (count > worst)
            {
                worst = count;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(buckets.Count, Is.GreaterThan(keys.Length / 4),
                $"{keys.Length} distinct 16-byte keys collapsed onto {buckets.Count} buckets of {capacity} — every one of them shares a probe chain");
            Assert.That(worst, Is.LessThan(MaxWorstProbes),
                $"the fullest bucket holds {worst} keys; a healthy hash at this load holds a handful");
        });
    }

    private static TestCaseData Family(string name, Func<int, long> gen)
    {
        var keys = new long[KeyCount];
        for (var i = 0; i < KeyCount; i++)
        {
            keys[i] = gen(i + 1);
        }
        return new TestCaseData(keys).SetName($"KeysSpreadAcrossBuckets({name})");
    }

    [Test]
    [TestCaseSource(nameof(KeyFamilies))]
    public void KeysSpreadAcrossBuckets(long[] keys)
    {
        // Distinct keys are a premise of the measurement, not a property under test: duplicates would share a bucket under ANY hash and would silently turn a
        // distribution assertion into a tautology.
        Assert.That(new HashSet<long>(keys).Count, Is.EqualTo(keys.Length), "PREMISE: the family generates distinct keys");

        var (mean, worst, distinctBuckets) = MeasureProbes(keys);

        Assert.Multiple(() =>
        {
            Assert.That(mean, Is.LessThan(MaxMeanProbes),
                $"mean probe length {mean:F2} — the map has degenerated into a linear scan. Every consumer that masks the low bits of the hash is affected.");
            Assert.That(worst, Is.LessThan(MaxWorstProbes), $"worst-case probe length {worst}");
            Assert.That(distinctBuckets, Is.GreaterThan(keys.Length / 4),
                $"{distinctBuckets:N0} distinct home buckets for {keys.Length:N0} keys — the hash is mapping the family onto a handful of slots");
        });
    }

    /// <summary>
    /// Replays <c>HashMap</c>'s probe sequence — <c>hash &amp; (capacity-1)</c> then linear <c>+1</c> — over a table sized the way the map sizes itself.
    /// </summary>
    /// <remarks>
    /// A replay rather than the real <c>HashMap</c> because the assertion is about probe COUNT, which the map does not expose; measuring wall-clock instead
    /// would make the test a performance test, and those are not stable enough to gate a merge on.
    /// </remarks>
    private static (double Mean, int Worst, int DistinctBuckets) MeasureProbes(long[] keys)
    {
        var capacity = 4;
        while (capacity < (int)(keys.Length / 0.75) + 1)
        {
            capacity <<= 1;
        }

        var mask = capacity - 1;
        var occupied = new bool[capacity];
        var homes = new HashSet<int>();
        long total = 0;
        var worst = 0;

        foreach (var key in keys)
        {
            var hash = HashUtils.ComputeHash(key);
            var idx = (int)(hash & (uint)mask);
            homes.Add(idx);

            var probes = 1;
            while (occupied[idx])
            {
                idx = (idx + 1) & mask;
                probes++;
                if (probes > capacity)
                {
                    Assert.Fail("probe sequence wrapped the whole table — the hash maps this family onto a single bucket");
                }
            }

            occupied[idx] = true;
            total += probes;
            worst = Math.Max(worst, probes);
        }

        return ((double)total / keys.Length, worst, homes.Count);
    }

    /// <summary>The 4-byte path shares the 8-byte path's shape and its failure mode; a constant low byte is the 32-bit analogue of the routing id.</summary>
    [Test]
    public void FourByteKeys_WithConstantLowBits_SpreadAcrossBuckets()
    {
        var keys = new long[KeyCount];
        for (var i = 0; i < KeyCount; i++)
        {
            keys[i] = ((i + 1) << 8) | 0x3F;
        }

        var intKeys = new int[KeyCount];
        for (var i = 0; i < KeyCount; i++)
        {
            intKeys[i] = (int)keys[i];
        }

        var capacity = 4;
        while (capacity < (int)(KeyCount / 0.75) + 1)
        {
            capacity <<= 1;
        }

        var mask = capacity - 1;
        var homes = new HashSet<int>();
        foreach (var key in intKeys)
        {
            homes.Add((int)(HashUtils.ComputeHash(key) & (uint)mask));
        }

        Assert.That(homes.Count, Is.GreaterThan(KeyCount / 4),
            $"{homes.Count:N0} distinct home buckets for {KeyCount:N0} 4-byte keys with constant low bits");
    }
}
