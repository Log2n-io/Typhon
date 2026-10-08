using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The 8-byte key hash of the entity map: runs of consecutive keys stay together, and every key pattern the engine issues still spreads evenly.
/// </summary>
/// <remarks>
/// Entity keys are a counter. A hash that scatters consecutive keys over the whole map makes a batch of spawns dirty one map page per entity; one that
/// keeps them in order but takes the key's low bits as they are overloads buckets as soon as keys are issued unevenly — key blocks leave every block's
/// tail unissued. The spread is measured where the map runs — 7 entries per bucket at stride 256, load factor 0.75 — and held to a random hash's
/// measured on the same keys: at that load a random hash already overflows about one bucket in six, so an absolute bound would hide a hash several times
/// worse.
/// </remarks>
[TestFixture]
class EntityMapHashTests
{
    private const int Capacity = 7;
    private const int Buckets = 1 << 16;
    private const int Keys = (int)(Buckets * Capacity * 0.75);   // exactly the split threshold

    private static uint Hash(long key) => RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(key);

    [Test]
    [VerifiesRule("EMAP-01")]
    public void AnAlignedRunOfKeys_FillsOneAlignedBlockOfBuckets()
    {
        const int run = 1 << RawValuePagedHashMap<long, PersistentStore>.HashRunBits;
        const uint mask = (1u << 20) - 1;
        var rng = new Random(1205);
        for (var r = 0; r < 64; r++)
        {
            var first = rng.NextInt64(0, 1L << 40) & ~(long)(run - 1);
            var buckets = new HashSet<uint>();
            var min = uint.MaxValue;
            for (var k = first; k < first + run; k++)
            {
                var b = Hash(k) & mask;
                buckets.Add(b);
                min = Math.Min(min, b);
            }

            Assert.That(buckets.Count, Is.EqualTo(run), $"the run at {first} shares buckets");
            Assert.That(min % run, Is.Zero, $"the run at {first} does not start a block");
            foreach (var b in buckets)
            {
                Assert.That(b - min, Is.LessThan((uint)run), $"the run at {first} leaves its block");
            }
        }
    }

    private static IEnumerable<TestCaseData> Patterns()
    {
        yield return new TestCaseData((Func<long[]>)(() => Sequential(Keys))).SetArgDisplayNames("sequential");
        yield return new TestCaseData((Func<long[]>)(() => Blocks(Keys, 16, 1))).SetArgDisplayNames("blocks of 16, 1 issued");
        yield return new TestCaseData((Func<long[]>)(() => Blocks(Keys, 16, 3))).SetArgDisplayNames("blocks of 16, 3 issued");
        yield return new TestCaseData((Func<long[]>)(() => Blocks(Keys, 64, 5))).SetArgDisplayNames("blocks of 64, 5 issued");
        yield return new TestCaseData((Func<long[]>)(() => Strided(Keys, 256))).SetArgDisplayNames("stride 256");
        yield return new TestCaseData((Func<long[]>)(() => Churned(Sequential(2 * Keys)))).SetArgDisplayNames("half churned away");
        yield return new TestCaseData((Func<long[]>)(() => RandomKeys(Keys))).SetArgDisplayNames("random");
    }

    [TestCaseSource(nameof(Patterns))]
    [VerifiesRule("EMAP-01")]
    public void EveryIssuedKeyPattern_SpreadsLikeARandomHash(Func<long[]> pattern) => AssertSpreadsLikeARandomHash(pattern(), Hash);

    /// <summary>The verifier: overflow and the fullest bucket within a margin of a random hash's on the same keys.</summary>
    private static void AssertSpreadsLikeARandomHash(long[] keys, Func<long, uint> hash)
    {
        var overflow = OverflowFraction(keys, hash, out var maxLoad);
        var randomOverflow = OverflowFraction(keys, RandomHash, out var randomMaxLoad);
        Assert.That(overflow, Is.LessThanOrEqualTo(randomOverflow * 1.15 + 0.005),
            $"spreads worse than a random hash: {overflow:P2} of the buckets need an overflow chunk, against {randomOverflow:P2}");
        Assert.That(maxLoad, Is.LessThanOrEqualTo(randomMaxLoad + 5),
            $"spreads worse than a random hash: a bucket holds {maxLoad} entries, against {randomMaxLoad}");
    }

    /// <summary>The reference: SplitMix64's finalizer, a full-avalanche mix of the whole key.</summary>
    private static uint RandomHash(long key)
    {
        var x = (ulong)key + 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return (uint)((x ^ (x >> 31)) >> 32);
    }

    /// <summary>The verifier can fail: the key's own low bits, the obvious locality hash, overload the ones a partly-issued block uses.</summary>
    [Test]
    [RuleMutant("EMAP-01")]
    public void Mutant_TheKeysOwnLowBits_FailTheSpread()
        => RuleMutants.AssertDetects("EMAP-01", "spreads worse than a random hash", () => AssertSpreadsLikeARandomHash(Blocks(Keys, 16, 3), key => (uint)key));

    private static double OverflowFraction(long[] keys, Func<long, uint> hash, out int maxLoad)
    {
        var load = new int[Buckets];
        foreach (var k in keys)
        {
            load[hash(k) & (Buckets - 1)]++;
        }

        long overflowChunks = 0;
        maxLoad = 0;
        foreach (var l in load)
        {
            if (l > Capacity)
            {
                overflowChunks += (l - 1) / Capacity;
            }

            maxLoad = Math.Max(maxLoad, l);
        }

        return (double)overflowChunks / Buckets;
    }

    private static long[] Sequential(int n)
    {
        var keys = new long[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = i + 1;
        }

        return keys;
    }

    private static long[] Blocks(int n, int block, int issued)
    {
        var keys = new long[n];
        long start = 0;
        for (var i = 0; i < n; start += block)
        {
            for (var j = 0; j < issued && i < n; j++)
            {
                keys[i++] = start + j + 1;
            }
        }

        return keys;
    }

    private static long[] Strided(int n, int stride)
    {
        var keys = new long[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = (long)i * stride + 1;
        }

        return keys;
    }

    private static long[] Churned(long[] all)
    {
        var rng = new Random(945);
        var kept = new List<long>(all.Length / 2);
        foreach (var k in all)
        {
            if (rng.Next(2) == 0)
            {
                kept.Add(k);
            }
        }

        return kept.ToArray();
    }

    private static long[] RandomKeys(int n)
    {
        var rng = new Random(7);
        var keys = new long[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = rng.NextInt64(1, 1L << 48);
        }

        return keys;
    }
}
