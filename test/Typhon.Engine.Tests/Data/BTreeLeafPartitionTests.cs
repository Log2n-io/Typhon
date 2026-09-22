using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>PartitionByLeafBoundaries</c> — the split that lets W workers apply one batch concurrently with no latch (#872 step 6, §5.5).
/// </summary>
/// <remarks>
/// <para>
/// The property under test is <b>leaf disjointness</b>, and it is checked against an independently computed leaf set rather than against the partitioner's
/// own bookkeeping: every key in a part is resolved to its owning leaf with <c>GetLeafChunkIdFor</c>, and the resulting per-part sets are asserted pairwise
/// disjoint. A partitioner that mis-snapped would produce parts that still cover the batch and still look contiguous — only the leaf sets would overlap.
/// </para>
/// <para>
/// This matters more than it looks, because it is the ONLY thing standing between the fence phase and two workers writing the same 256 B node with no latch,
/// no version validation and no restart handling. The bulk descent writes leaves and nothing else (its only mutations are <c>SetValueOnly</c> and
/// <c>UpdateInBuffer</c>), so "no two workers write the same node" is exactly "no two parts share a leaf".
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class BTreeLeafPartitionTests
{
    private const int TreeSize = 20_000;

    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                var raw = $"part_{TestContext.CurrentContext.Test.Name}".Replace("(", "_").Replace(")", "").Replace(",", "_");
                options.DatabaseName = raw[..Math.Min(63, raw.Length)];
                options.DatabaseCacheSize = (ulong)(4_096 * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private delegate void TreeAction(IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    private delegate void MultiTreeAction(IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor);

    /// <summary>Keys are <c>i * 10</c> for i in 1..<see cref="TreeSize"/>, so gaps exist between adjacent keys and an absent key is easy to name.</summary>
    private unsafe void WithTree(TreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                var tree = new IntSingleBTree<PersistentStore>(segment);
                for (var i = 1; i <= TreeSize; i++)
                {
                    tree.Add(i * 10, i, ref accessor);
                }

                body(tree, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private unsafe void WithMultiTree(MultiTreeAction body)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 1_000, sizeof(Index32Chunk));

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                body(new IntMultipleBTree<PersistentStore>(segment), ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private static BTreeValueUpdate<int>[] ScatteredBatch(int count, int seed)
    {
        var rng = new Random(seed);
        var keys = new SortedSet<int>();
        while (keys.Count < count)
        {
            keys.Add(rng.Next(1, TreeSize + 1) * 10);
        }

        var batch = new BTreeValueUpdate<int>[count];
        var i = 0;
        foreach (var key in keys)
        {
            batch[i] = new BTreeValueUpdate<int>(key, 1_000_000 + i);
            i++;
        }

        return batch;
    }

    /// <summary>Asserts the parts are a contiguous, ordered, gap-free cover of <paramref name="length"/> entries.</summary>
    private static void AssertIsCover(Span<int> boundaries, int parts, int length)
    {
        Assert.That(boundaries[0], Is.Zero, "the first part must start at the beginning of the batch");
        Assert.That(boundaries[parts], Is.EqualTo(length), "the last part must end at the end of the batch");
        for (var p = 0; p < parts; p++)
        {
            Assert.That(boundaries[p + 1], Is.GreaterThan(boundaries[p]), $"part {p} is empty, which costs a dispatch and buys nothing");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // The property that carries the weight: parts never share a leaf
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void Partition_PartsTouchDisjointLeafSets(int desiredParts)
    {
        var batch = ScatteredBatch(4_000, 90_210 + desiredParts);

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[desiredParts + 1];
            var parts = tree.PartitionByLeafBoundaries(batch, desiredParts, boundaries, ref accessor);

            Assert.That(parts, Is.GreaterThan(1), "4 000 scattered keys over a 20 000-entry tree span far more leaves than there are parts");
            AssertIsCover(boundaries, parts, batch.Length);

            // The oracle: resolve every key to its owning leaf INDEPENDENTLY of the partitioner, then check the per-part sets do not intersect. `owner` maps
            // a leaf chunk id to the first part that reached it, so an overlap names both parts rather than merely failing.
            var owner = new Dictionary<int, int>();
            var leavesSeen = 0;
            for (var p = 0; p < parts; p++)
            {
                for (var i = boundaries[p]; i < boundaries[p + 1]; i++)
                {
                    var leaf = tree.GetLeafChunkIdFor(batch[i].Key, ref accessor);
                    if (owner.TryGetValue(leaf, out var firstPart))
                    {
                        if (firstPart != p)
                        {
                            Assert.Fail($"leaf {leaf} is reached by both part {firstPart} and part {p}: two workers would write the same node with no latch");
                        }

                        continue;
                    }

                    owner[leaf] = p;
                    leavesSeen++;
                }
            }

            Assert.That(leavesSeen, Is.GreaterThan(parts), "the partition must span more leaves than parts or this test proves nothing");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_BoundaryKeysLandInDifferentLeaves()
    {
        // The same guarantee stated at the seam rather than over the whole batch: the last entry on the left of a cut and the first on its right must not
        // share a leaf. This is the assertion that fails first if the snap is off by one entry.
        var batch = ScatteredBatch(4_000, 55_555);

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
            Assert.That(parts, Is.GreaterThan(1));

            for (var p = 1; p < parts; p++)
            {
                var cut = boundaries[p];
                var left = tree.GetLeafChunkIdFor(batch[cut - 1].Key, ref accessor);
                var right = tree.GetLeafChunkIdFor(batch[cut].Key, ref accessor);
                Assert.That(right, Is.Not.EqualTo(left), $"the cut at entry {cut} falls inside leaf {left} instead of on its edge");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Balance — the reason for splitting by count before snapping
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void Partition_SplitsByCount_NotByKeySpace()
    {
        // §5.5 rejects partitioning by the root's separators because it "balances terribly on clustered keys", which is precisely the distribution
        // re-clustering produces. This batch is that adversary: 90 % of the entries sit in the bottom 18 % of the key space. A key-space split would give one
        // part almost everything; the count split plus snap must not.
        //
        // The dense half is a contiguous run rather than a rejection sample. Drawing 3 600 DISTINCT keys from a range of 2 000 is not slow, it never
        // terminates — and a hung fixture surfaces as "Test host process crashed" with every other test in the file reported as passed, which reads as an
        // engine fault rather than as arithmetic in the test.
        var keys = new SortedSet<int>();
        for (var i = 1; i <= 3_600; i++)
        {
            keys.Add(i * 10);
        }

        var rng = new Random(4_242);
        var sparseFloor = 3_601;
        while (keys.Count < 4_000)
        {
            keys.Add(rng.Next(sparseFloor, TreeSize + 1) * 10);
        }

        var batch = new BTreeValueUpdate<int>[keys.Count];
        var n = 0;
        foreach (var key in keys)
        {
            batch[n] = new BTreeValueUpdate<int>(key, n);
            n++;
        }

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
            AssertIsCover(boundaries, parts, batch.Length);

            var ideal = (double)batch.Length / parts;
            for (var p = 0; p < parts; p++)
            {
                var size = boundaries[p + 1] - boundaries[p];
                // The snap can only ever push a boundary FORWARD, and never past a whole leaf's worth of entries, so parts stay within a leaf of nominal.
                // Two-fold is a loose bound deliberately: the point is that no part gets an order of magnitude more than its share, not that the split is
                // exact.
                Assert.That(size, Is.LessThan(ideal * 2), $"part {p} holds {size} of {batch.Length} entries against a nominal {ideal:F0}");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Degenerate shapes
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void Partition_EmptyBatch_ProducesNoParts()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(ReadOnlySpan<BTreeValueUpdate<int>>.Empty, 8, boundaries, ref accessor);
            Assert.That(parts, Is.Zero, "an empty batch must produce no work items at all, not one empty one");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    [TestCase(0)]
    [TestCase(1)]
    public void Partition_OnePartOrFewer_ReturnsTheWholeBatch(int desiredParts)
    {
        var batch = ScatteredBatch(500, 77);

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[Math.Max(desiredParts, 1) + 1];
            var parts = tree.PartitionByLeafBoundaries(batch, desiredParts, boundaries, ref accessor);
            Assert.That(parts, Is.EqualTo(1));
            AssertIsCover(boundaries, parts, batch.Length);
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_MorePartsThanEntries_NeverProducesAnEmptyPart()
    {
        var batch = ScatteredBatch(3, 31);

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[33];
            var parts = tree.PartitionByLeafBoundaries(batch, 32, boundaries, ref accessor);
            Assert.That(parts, Is.LessThanOrEqualTo(batch.Length));
            AssertIsCover(boundaries, parts, batch.Length);
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_ClusteredIntoOneLeaf_CollapsesToOnePart()
    {
        // A batch whose keys all descend to the same leaf CANNOT be split without two workers writing that leaf, so the only correct answer is one part.
        // Silently emitting eight would be the defect this whole mechanism exists to prevent.
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var probe = tree.GetLeafChunkIdFor(5_000, ref accessor);
            var keys = new List<int>();
            for (var k = 5_000; k <= 6_000 && keys.Count < 64; k += 10)
            {
                if (tree.GetLeafChunkIdFor(k, ref accessor) == probe)
                {
                    keys.Add(k);
                }
            }

            Assert.That(keys.Count, Is.GreaterThan(4), "the fixture needs several keys sharing one leaf for this case to mean anything");

            var batch = new BTreeValueUpdate<int>[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                batch[i] = new BTreeValueUpdate<int>(keys[i], i);
            }

            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
            Assert.That(parts, Is.EqualTo(1), "every key shares one leaf, so any split would hand two workers the same node");
            AssertIsCover(boundaries, parts, batch.Length);
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_AbsentKeys_StillSplitDisjointly()
    {
        // Keys the tree does not hold still route to a leaf, and the part that owns them will still open that leaf. Disjointness therefore has to hold for
        // absent keys too, or a part whose entries all miss can still collide with its neighbour.
        var batch = new BTreeValueUpdate<int>[2_000];
        for (var i = 0; i < batch.Length; i++)
        {
            batch[i] = new BTreeValueUpdate<int>(i * 90 + 7, i);    // never a multiple of 10, so never present
        }

        WithTree((IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
            AssertIsCover(boundaries, parts, batch.Length);

            var owner = new Dictionary<int, int>();
            for (var p = 0; p < parts; p++)
            {
                for (var i = boundaries[p]; i < boundaries[p + 1]; i++)
                {
                    var leaf = tree.GetLeafChunkIdFor(batch[i].Key, ref accessor);
                    if (owner.TryGetValue(leaf, out var firstPart) && firstPart != p)
                    {
                        Assert.Fail($"leaf {leaf} is reached by parts {firstPart} and {p} on a batch of absent keys");
                    }

                    owner[leaf] = p;
                }
            }
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void Partition_BoundariesSpanTooSmall_Throws()
    {
        WithTree(static (IntSingleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            var batch = new[] { new BTreeValueUpdate<int>(10, 1), new BTreeValueUpdate<int>(20, 2) };
            var localAccessor = accessor;
            var boundaries = new int[4];    // 8 parts need 9 slots
            Assert.Throws<InvalidOperationException>(() => tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref localAccessor));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AllowMultiple — a key's entries must never straddle a cut
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15_000)]
    public void Partition_MultiValueBatch_KeepsEachKeysEntriesInOnePart()
    {
        // Two parts sharing a key would open the same leaf slot AND the same element buffer. The snap compares against a key bound, so entries sharing a key
        // are either all below it or all above — but that is an argument, and this is the check.
        WithMultiTree(static (IntMultipleBTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor) =>
        {
            const int Keys = 500;
            const int PerKey = 8;
            var batch = new BTreeMultiValueUpdate<int>[Keys * PerKey];
            var n = 0;
            for (var k = 1; k <= Keys; k++)
            {
                var key = k * 10;
                for (var e = 0; e < PerKey; e++)
                {
                    var value = k * 1_000 + e;
                    var elementId = tree.Add(key, value, ref accessor);
                    batch[n++] = new BTreeMultiValueUpdate<int>(key, elementId, value, value + 500_000);
                }
            }

            var boundaries = new int[9];
            var parts = tree.PartitionByLeafBoundaries(batch, 8, boundaries, ref accessor);
            Assert.That(parts, Is.GreaterThan(1));
            AssertIsCover(boundaries, parts, batch.Length);

            for (var p = 1; p < parts; p++)
            {
                var cut = boundaries[p];
                Assert.That(batch[cut].Key, Is.Not.EqualTo(batch[cut - 1].Key),
                    $"the cut at entry {cut} splits key {batch[cut].Key} across two parts");
            }
        });
    }
}
