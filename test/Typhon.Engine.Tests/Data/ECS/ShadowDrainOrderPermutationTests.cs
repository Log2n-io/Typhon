using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>ArchetypeClusterState.BuildDrainOrder</c> — the counting sort that puts shadow entries in ascending cluster order (#882).
/// </summary>
/// <remarks>
/// <para><b>This fixture exists because an ablation caught its absence.</b> The engine-level drain tests stayed GREEN under a permutation whose scatter
/// dropped 31 of every 32 entries, because the query planner served those assertions from a scan rather than from the index. A permutation is a data
/// structure with an exact contract — every input index exactly once — and it deserves a test that says so directly rather than one that hopes an integration
/// notices.</para>
/// <para>Every case checks the two properties that matter: the result is a PERMUTATION of <c>[0, count)</c>, and it is <b>non-decreasing by cluster id and
/// stable within a cluster</b>. Stability is not cosmetic — it is what keeps a drain's behaviour a function of the data rather than of the sort.</para>
/// </remarks>
[TestFixture]
class ShadowDrainOrderPermutationTests
{
    private static FieldShadowBuffer BufferOf(IReadOnlyList<int> clusterIds)
    {
        var buffer = new FieldShadowBuffer(Math.Max(256, clusterIds.Count));
        for (var i = 0; i < clusterIds.Count; i++)
        {
            // ChunkId is the entityIndex: cluster in the high bits, slot in the low six. The slot is irrelevant to the sort, so vary it to prove that.
            buffer.Append((clusterIds[i] << 6) | (i & 63), default, default);
        }

        return buffer;
    }

    private static void AssertOrderedPermutation(IReadOnlyList<int> clusterIds)
    {
        var buffer = BufferOf(clusterIds);
        var count = clusterIds.Count;
        int[] order = [];
        int[] counts = [];

        var result = ArchetypeClusterState.BuildDrainOrder(buffer, count, ref order, ref counts);

        var seen = new bool[count];
        var previousCluster = int.MinValue;
        var previousIndex = -1;
        for (var k = 0; k < count; k++)
        {
            var e = result[k];
            Assert.That(e, Is.InRange(0, count - 1), $"position {k} holds {e}, which is not an index into the buffer");
            Assert.That(seen[e], Is.False, $"index {e} appears more than once — entries would be drained twice and others not at all");
            seen[e] = true;

            var cluster = clusterIds[e];
            Assert.That(cluster, Is.GreaterThanOrEqualTo(previousCluster),
                $"position {k}: cluster {cluster} follows {previousCluster}, so the order is not ascending");
            if (cluster == previousCluster)
            {
                Assert.That(e, Is.GreaterThan(previousIndex), "within one cluster the original append order must be preserved");
            }

            previousCluster = cluster;
            previousIndex = e;
        }

        for (var e = 0; e < count; e++)
        {
            Assert.That(seen[e], Is.True, $"index {e} never appears — its shadow entry would never be drained");
        }
    }

    [Test]
    public void AnEmptyDrainReturnsNothingRatherThanWrappingItsSentinels()
    {
        // Without the `count <= 0` guard the min/max sentinels make `max - min + 1` wrap to 2, and the method walks a histogram it never filled. Callers
        // skip an empty buffer today, but the method is `internal static` and directly callable, so the edge belongs to the method.
        var buffer = BufferOf([]);
        int[] order = [];
        int[] counts = [];

        var result = ArchetypeClusterState.BuildDrainOrder(buffer, 0, ref order, ref counts);

        Assert.That(result.Length, Is.EqualTo(0), "an empty drain orders nothing");
    }

    [Test]
    public void OneEntry() => AssertOrderedPermutation([5]);

    [Test]
    public void EveryEntryInOneCluster() => AssertOrderedPermutation([9, 9, 9, 9, 9, 9, 9, 9]);

    [Test]
    public void AlreadyAscending() => AssertOrderedPermutation([0, 1, 2, 3, 4, 5]);

    [Test]
    public void StrictlyDescending() => AssertOrderedPermutation([9, 8, 7, 6, 5, 4, 3, 2, 1, 0]);

    [Test]
    public void ClustersDoNotStartAtZero() => AssertOrderedPermutation([1000, 1002, 1001, 1000, 1002]);

    [Test]
    public void ASparseSpanWithLargeHoles() => AssertOrderedPermutation([0, 100_000, 7, 100_000, 3, 0]);

    [Test]
    public void ManyEntriesPerClusterAcrossManyClusters()
    {
        // The realistic shape: ~32 entities to a cluster, written in scrambled order. This is the case the engine hits, and the one a bucket-offset mistake
        // corrupts while the small cases above stay green.
        var rng = new Random(4242);
        var ids = new List<int>(2_000);
        for (var cluster = 0; cluster < 64; cluster++)
        {
            for (var slot = 0; slot < 31; slot++)
            {
                ids.Add(cluster);
            }
        }

        for (var i = ids.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }

        AssertOrderedPermutation(ids);
    }

    [Test]
    public void CrossesTheShadowBufferBlockBoundary()
    {
        // FieldShadowBuffer stores entries in 4096-entry blocks. A permutation that is correct inside one block and wrong across two is exactly the defect
        // this size is chosen to catch.
        var rng = new Random(7);
        var ids = new List<int>(5_000);
        for (var i = 0; i < 5_000; i++)
        {
            ids.Add(rng.Next(0, 200));
        }

        AssertOrderedPermutation(ids);
    }

    [Test]
    public void TheScratchBuffersAreReusableAcrossCallsWithDifferentShapes()
    {
        // The scratch is per-archetype and lives for the process. A histogram left dirty by one call, or an order array read past the current count,
        // corrupts a LATER tick — so the contract is that consecutive calls with unrelated spans each return a correct permutation.
        int[] order = [];
        int[] counts = [];
        var rng = new Random(11);

        int[][] shapes =
        [
            [3, 1, 2],
            [500, 501, 500, 502, 501],
            [0],
            [7, 7, 7, 7, 7, 7, 7, 7, 7, 7],
            [90_000, 1, 90_000, 1],
        ];

        foreach (var shape in shapes)
        {
            var buffer = BufferOf(shape);
            var result = ArchetypeClusterState.BuildDrainOrder(buffer, shape.Length, ref order, ref counts);

            var seen = new bool[shape.Length];
            var previous = int.MinValue;
            for (var k = 0; k < shape.Length; k++)
            {
                var e = result[k];
                Assert.That(seen[e], Is.False, "no index twice, on any call");
                seen[e] = true;
                Assert.That(shape[e], Is.GreaterThanOrEqualTo(previous), "ascending, on every call");
                previous = shape[e];
            }
        }

        // And a large call after several small ones, to catch a histogram whose growth lost the clear.
        var big = new int[3_000];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = rng.Next(0, 400);
        }

        var bigBuffer = BufferOf(big);
        var bigResult = ArchetypeClusterState.BuildDrainOrder(bigBuffer, big.Length, ref order, ref counts);
        var bigSeen = new bool[big.Length];
        var last = int.MinValue;
        for (var k = 0; k < big.Length; k++)
        {
            var e = bigResult[k];
            Assert.That(bigSeen[e], Is.False, "no index twice after the scratch has been resized");
            bigSeen[e] = true;
            Assert.That(big[e], Is.GreaterThanOrEqualTo(last), "ascending after the scratch has been resized");
            last = big[e];
        }
    }
}
