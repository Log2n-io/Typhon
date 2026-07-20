using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class HashMapPartitionTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1. Partition Correctness — all elements visited exactly once
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCase(100, 1)]
    [TestCase(100, 2)]
    [TestCase(100, 4)]
    [TestCase(100, 8)]
    [TestCase(1000, 16)]
    [TestCase(10000, 28)]
    public void PartitionEnumeration_AllElementsVisitedExactlyOnce(int entityCount, int partitions)
    {
        using var map = new HashMap<long>(entityCount * 2);
        for (int i = 0; i < entityCount; i++)
        {
            map.TryAdd(i * 7 + 1); // non-sequential keys for hash distribution
        }

        var collected = new HashSet<long>();
        int totalYielded = 0;

        for (int p = 0; p < partitions; p++)
        {
            var enumerator = map.GetPartitionEnumerator(p, partitions);
            while (enumerator.MoveNext())
            {
                Assert.That(collected.Add(enumerator.Current), Is.True,
                    $"Duplicate: {enumerator.Current} in partition {p}");
                totalYielded++;
            }
        }

        Assert.That(totalYielded, Is.EqualTo(entityCount), "Total yielded mismatch");
        Assert.That(collected.Count, Is.EqualTo(entityCount), "Unique count mismatch");
    }

    [Test]
    public void PartitionEnumeration_EmptyMap_AllPartitionsEmpty()
    {
        using var map = new HashMap<long>();

        for (int p = 0; p < 4; p++)
        {
            var enumerator = map.GetPartitionEnumerator(p, 4);
            Assert.That(enumerator.MoveNext(), Is.False);
        }
    }

    [Test]
    public void PartitionEnumeration_SinglePartition_EqualsFullEnumeration()
    {
        using var map = new HashMap<long>();
        for (int i = 0; i < 500; i++)
        {
            map.TryAdd(i);
        }

        var fromFull = new HashSet<long>();
        foreach (var key in map)
        {
            fromFull.Add(key);
        }

        var fromPartition = new HashSet<long>();
        var enumerator = map.GetPartitionEnumerator(0, 1);
        while (enumerator.MoveNext())
        {
            fromPartition.Add(enumerator.Current);
        }

        Assert.That(fromPartition.SetEquals(fromFull), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Load Balance — variance across partitions
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCase(10000, 4)]
    [TestCase(10000, 8)]
    [TestCase(10000, 16)]
    [TestCase(10000, 28)]
    public void PartitionEnumeration_LoadBalanceVariance_Under20Percent(int entityCount, int partitions)
    {
        using var map = new HashMap<long>(entityCount * 2);
        for (int i = 0; i < entityCount; i++)
        {
            map.TryAdd(i);
        }

        var counts = new int[partitions];
        for (int p = 0; p < partitions; p++)
        {
            var enumerator = map.GetPartitionEnumerator(p, partitions);
            while (enumerator.MoveNext())
            {
                counts[p]++;
            }
        }

        int total = counts.Sum();
        Assert.That(total, Is.EqualTo(entityCount));

        double expected = (double)entityCount / partitions;
        double maxDeviation = counts.Max(c => Math.Abs(c - expected) / expected);

        Assert.That(maxDeviation, Is.LessThan(0.20),
            $"Max partition deviation {maxDeviation:P1} exceeds 20%. Counts: [{string.Join(", ", counts)}]");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Concurrent partition enumeration (simulates parallel dispatch)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void PartitionEnumeration_ConcurrentReaders_AllElementsCollected()
    {
        const int entityCount = 5000;
        const int threadCount = 8;

        using var map = new HashMap<long>(entityCount * 2);
        for (int i = 0; i < entityCount; i++)
        {
            map.TryAdd(i);
        }

        var allCollected = new ConcurrentBag<long>();
        var barrier = new Barrier(threadCount);
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            barrier.SignalAndWait();
            try
            {
                var enumerator = map.GetPartitionEnumerator(threadIdx, threadCount);
                while (enumerator.MoveNext())
                {
                    allCollected.Add(enumerator.Current);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));

        var unique = new HashSet<long>(allCollected);
        Assert.That(unique.Count, Is.EqualTo(entityCount), "Not all entities collected");
        Assert.That(allCollected.Count, Is.EqualTo(entityCount), "Duplicate elements across partitions");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Clone correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Clone_IndependentCopy_MutationsDoNotAffectOriginal()
    {
        using var original = new HashMap<long>();
        for (int i = 0; i < 100; i++)
        {
            original.TryAdd(i);
        }

        using var clone = original.Clone();

        Assert.That(clone.Count, Is.EqualTo(original.Count));

        // Mutate clone
        clone.TryAdd(999);
        clone.TryRemove(0);

        // Original unchanged
        Assert.That(original.Count, Is.EqualTo(100));
        Assert.That(original.Contains(0), Is.True);
        Assert.That(original.Contains(999), Is.False);

        // Clone reflects mutations
        Assert.That(clone.Count, Is.EqualTo(100)); // +1 -1 = same
        Assert.That(clone.Contains(0), Is.False);
        Assert.That(clone.Contains(999), Is.True);
    }

    [Test]
    public void Clone_EmptyMap_ReturnsEmptyClone()
    {
        using var original = new HashMap<long>();
        using var clone = original.Clone();
        Assert.That(clone.Count, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. IEnumerable<T> interface
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void IEnumerable_LinqWorks()
    {
        using var map = new HashMap<long>();
        for (int i = 0; i < 50; i++)
        {
            map.TryAdd(i);
        }

        // LINQ uses IEnumerable<T> — tests the BoxedEnumerator path
        var list = ((IEnumerable<long>)map).ToList();
        Assert.That(list.Count, Is.EqualTo(50));

        var sorted = list.OrderBy(x => x).ToList();
        for (int i = 0; i < 50; i++)
        {
            Assert.That(sorted[i], Is.EqualTo(i));
        }
    }
}
