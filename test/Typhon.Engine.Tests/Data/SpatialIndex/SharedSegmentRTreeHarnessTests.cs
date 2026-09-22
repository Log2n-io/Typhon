using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Falsification harness for #872 step 9's shared-segment per-cell R-Trees. Deliberately standalone: one segment, one tree engine, no grid, no archetype,
/// no fence, no <see cref="DatabaseEngine"/> — so a red result here indicts the R-Tree adaptation and nothing else.
/// </summary>
/// <remarks>
/// The step-9 design rests on three claims that are cheap to test and expensive to discover later, and this fixture exists to settle them BEFORE any of
/// <c>ArchetypeClusterState</c> is touched. It is written to fail loudly on the current code where the current code is genuinely unfit — a red test here is
/// the deliverable, not a defect.
/// </remarks>
[TestFixture]
unsafe class SharedSegmentRTreeHarnessTests
{
    private IServiceProvider _serviceProvider;
    private string _testDatabaseDir;

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', '"', '.', '<', '>', '+', ' ' })
            {
                testName = testName.Replace(c, '_');
            }
            return $"SSH{(testName.Length > 30 ? testName[^30..] : testName)}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SharedSegmentRTreeHarness");
        Directory.CreateDirectory(_testDatabaseDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseDirectory = _testDatabaseDir;
                o.DatabaseCacheSize = 4096UL * PagedMMF.PageSize;
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        try
        {
            foreach (var file in Directory.GetFiles(_testDatabaseDir))
            {
                try { File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// A tree must refuse to refit a leaf that belongs to a DIFFERENT tree in the same segment, and must not walk that tree's parent chain.
    /// </summary>
    /// <remarks>
    /// <para><b>What the guard is actually for, stated precisely because the first version of this test overstated it.</b> Before step 9 a chunk id could
    /// only name a node of the one tree over its segment, so <c>RefitLeafAndAncestors</c> could take "it is a leaf" as proof of ownership. With many trees per
    /// segment a chunk this tree freed can be reallocated as another cell tree's leaf and passes that test wearing the other tree's data; the walk then climbs
    /// ITS parent chain, since every root in a shared segment has <c>ParentChunkId == 0</c> and the terminator says nothing about whose chain it was.</para>
    /// <para><b>It is NOT silent data corruption, and claiming so would be wrong.</b> The refit recomputes each node's MBR from that node's own entries, so
    /// running it over a well-formed foreign tree writes back the values already there — the observable VALUES do not change, which is exactly why the first
    /// version of this test could not fail. What does happen is that tree A takes WRITE LATCHES on tree B's nodes, all the way to B's root. That is the real
    /// defect: an exclusive-window discipline (<c>PC-01</c>) that reasons per tree is void if one tree can latch another's spine, and a chunk id that is stale
    /// rather than reallocated has no well-formed contents to recompute from and no bounded parent chain to walk.</para>
    /// <para><b>So the assertion is on the OLC version, not on the coordinates.</b> A write latch bumps it whether or not the value changes, which makes
    /// "was this node touched" observable where "did this node change" is not.</para>
    /// <para><b>Ablation.</b> Disabling the <c>OwnsNode</c> guard reddens this — B's root version advances because A latched it.</para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void RefitLeafAndAncestors_RefusesALeafBelongingToAnotherTreeOnTheSameSegment()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df32);
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);

            var treeA = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);
            var treeB = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);
            treeA.PayloadBackPointers = new int[512];
            treeB.PayloadBackPointers = new int[512];

            var accessor = segment.CreateChunkAccessor();
            try
            {
                // A is far from the origin, B is near it, and both are forced past one leaf so each owns an internal root with a real MBR to corrupt.
                for (int i = 0; i < 40; i++)
                {
                    treeA.Insert(100 + i, Box(desc, 900 + i, 900 + i, 904 + i, 904 + i), ref accessor);
                    treeB.Insert(200 + i, Box(desc, i, i, 4 + i, 4 + i), ref accessor);
                }

                var (foreignLeaf, _) = SpatialRTree<PersistentStore>.UnpackHandle(treeB.PayloadBackPointers[200]);

                // Preconditions, or the test proves nothing: the two trees must be distinct, B must have split (so its root is internal and carries a MBR
                // that a foreign refit would rewrite), and the leaf we hand to A must genuinely be B's.
                Assert.Multiple(() =>
                {
                    Assert.That(treeA.RootChunkId, Is.Not.EqualTo(treeB.RootChunkId), "the two trees must not share a root");
                    Assert.That(treeB.Depth, Is.GreaterThan(1), "tree B must have split, or its root is the leaf and there is no ancestor chain to walk");
                    Assert.That(foreignLeaf, Is.Not.EqualTo(treeA.RootChunkId));
                });

                int rootVersionBefore = ReadNodeVersion(treeB.RootChunkId, ref accessor);
                int leafVersionBefore = ReadNodeVersion(foreignLeaf, ref accessor);

                // The whole point: A is asked to refit a leaf it does not own.
                treeA.RefitLeafAndAncestors(foreignLeaf, ref accessor);

                int rootVersionAfter = ReadNodeVersion(treeB.RootChunkId, ref accessor);
                int leafVersionAfter = ReadNodeVersion(foreignLeaf, ref accessor);
                TestContext.Out.WriteLine($"OWNS foreignLeaf={foreignLeaf} rootB={treeB.RootChunkId} depthB={treeB.Depth} "
                    + $"leafVer {leafVersionBefore}->{leafVersionAfter} rootVer {rootVersionBefore}->{rootVersionAfter}");

                Assert.Multiple(() =>
                {
                    Assert.That(leafVersionAfter, Is.EqualTo(leafVersionBefore), "tree A write-latched a LEAF belonging to tree B");
                    Assert.That(rootVersionAfter, Is.EqualTo(rootVersionBefore), "tree A walked into tree B's ancestor chain and write-latched its root");
                });

                // And B still answers for everything it holds — the SQ-01 direction that a rewritten MBR would break.
                var whole = Box(desc, -1e6, -1e6, 1e6, 1e6);
                int found = 0;
                var e = treeB.QueryAABB(whole);
                try
                {
                    while (e.MoveNext())
                    {
                        found++;
                    }
                }
                finally
                {
                    e.Dispose();
                }
                Assert.That(found, Is.EqualTo(40), "tree B lost entries after a foreign refit");
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>A node's OLC version — bumped by any write latch, so it records that a node was TOUCHED even when the values written were identical.</summary>
    private static int ReadNodeVersion(int chunkId, ref ChunkAccessor<PersistentStore> accessor) =>
        SpatialNodeHelper.OlcVersionRef(accessor.GetChunkAddress(chunkId));

    private static double[] Box(SpatialNodeDescriptor desc, double minX, double minY, double maxX, double maxY)
    {
        var coords = new double[desc.CoordCount];
        int h = desc.CoordCount / 2;
        coords[0] = minX;
        coords[1] = minY;
        if (h == 3) { coords[2] = 0; }
        coords[h] = maxX;
        coords[h + 1] = maxY;
        if (h == 3) { coords[h + 2] = 1; }
        return coords;
    }

    /// <summary>
    /// <b>Claim 1 (7c).</b> With a payload back-pointer sink attached, an <c>Insert</c> handle stays valid across every leaf split and every swap-with-last
    /// removal — which is the precondition for <c>C5</c>'s escape-bound update existing at all.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured before the fix, on this exact fixture: 17 of 60 handles still correct, 20 of them pointing outside their leaf's live range.</b>
    /// <c>InsertWithSplit</c> scatters BOTH halves through the overlap-minimising permutation (<c>Split.cs:60</c> and <c>:65</c>), so an entry that stays in
    /// its original leaf still changes slot — it is not "half the handles move", it is very nearly all of them.</para>
    /// <para>The failure it produces is silent: a stale handle makes the next escape-bound update write cluster X's bound into cluster Y's slot, so Y's
    /// stored bound stops containing Y's entities (<c>CA-01</c>, hence an <c>SQ-01</c> false negative) while X's is never updated at all. <c>TreeValidator</c>
    /// passes throughout, because the tree is structurally perfect — only the addresses held outside it are wrong.</para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void Claim1_InsertHandlesSurviveSplitsAndRemovals()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df32);
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);
            var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);

            const int Count = 60;
            tree.PayloadBackPointers = new int[Count + 1];

            var accessor = segment.CreateChunkAccessor();
            try
            {
                // LeafCapacity is 13 for R3Df32 (11 before #872 step 13), so 60 entries forces several leaf splits and at least one root split.
                var rng = new Random(90210);
                for (long id = 1; id <= Count; id++)
                {
                    double x = rng.NextDouble() * 1000;
                    double y = rng.NextDouble() * 1000;
                    var (leaf, slot) = tree.Insert(id, Box(desc, x, y, x + 5, y + 5), ref accessor);
                    tree.PayloadBackPointers[id] = SpatialRTree<PersistentStore>.PackHandle(leaf, slot);
                }

                int afterInserts = CountCorrectHandles(tree, accessor, desc, 1, Count);
                TestContext.Out.WriteLine($"CLAIM1 afterInserts correct={afterInserts}/{Count} nodes={tree.NodeCount} depth={tree.Depth}");
                Assert.That(afterInserts, Is.EqualTo(Count), "every handle must name its own payload after the split storm");

                // Swap-with-last moves exactly one entry per removal, and it is the tree that repairs that handle. Remove a scattered third of the payloads.
                var removed = new HashSet<long>();
                for (long id = 3; id <= Count; id += 3)
                {
                    var (leaf, slot) = SpatialRTree<PersistentStore>.UnpackHandle(tree.PayloadBackPointers[id]);
                    tree.Remove(leaf, slot, ref accessor);
                    // Remove retires the handle itself now; assert that rather than overwriting it, or the test cannot tell a retired handle from a stale one.
                    Assert.That(SpatialRTree<PersistentStore>.IsNullHandle(tree.PayloadBackPointers[id]), Is.True,
                        $"Remove must retire payload {id}'s handle — a live-looking handle into a freed or reused slot is the defect this array prevents");
                    removed.Add(id);
                }

                int survivors = 0;
                int correct = 0;
                for (long id = 1; id <= Count; id++)
                {
                    if (removed.Contains(id)) { continue; }
                    survivors++;
                    if (HandleNames(tree, accessor, desc, id)) { correct++; }
                }

                TestContext.Out.WriteLine($"CLAIM1 afterRemovals correct={correct}/{survivors} removed={removed.Count} nodes={tree.NodeCount}");
                Assert.That(correct, Is.EqualTo(survivors), "swap-with-last must repair the handle of the entry it moved");
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static int CountCorrectHandles(SpatialRTree<PersistentStore> tree, ChunkAccessor<PersistentStore> accessor,
        in SpatialNodeDescriptor desc, long firstId, long lastId)
    {
        int correct = 0;
        for (long id = firstId; id <= lastId; id++)
        {
            if (HandleNames(tree, accessor, desc, id)) { correct++; }
        }
        return correct;
    }

    /// <summary>True when the payload's recorded handle still addresses the entry holding that payload.</summary>
    private static bool HandleNames(SpatialRTree<PersistentStore> tree, ChunkAccessor<PersistentStore> accessor, in SpatialNodeDescriptor desc, long id)
    {
        var (leaf, slot) = SpatialRTree<PersistentStore>.UnpackHandle(tree.PayloadBackPointers[id]);
        byte* nodeBase = accessor.GetChunkAddress(leaf);
        if (!SpatialNodeHelper.IsLeaf(nodeBase) || slot >= SpatialNodeHelper.GetCount(nodeBase))
        {
            return false;
        }
        return SpatialNodeHelper.ReadLeafEntityId(nodeBase, slot, desc) == id;
    }

    /// <summary>
    /// <b>Claim 2.</b> Two independent <c>Owned</c> trees allocating nodes from ONE segment stay correct — disjoint nodes, and neither answering for the
    /// other's payloads — through enough interleaved inserts to split both.
    /// </summary>
    /// <remarks>
    /// <para>This is the shared-segment premise, and the first version of this test asserted the wrong thing: that the two trees' <c>RootChunkId</c> values
    /// differ. They trivially do, because all four metadata values are already per-INSTANCE fields (<c>SpatialRTree.cs:88-94</c>) — chunk 0 is only a
    /// write-through mirror that nothing reads unless the tree is constructed with <c>load: true</c>.</para>
    /// <para>Which reframes the whole question. If this passes, multi-tenancy needs NO metadata detachment for a transient tree that is never reloaded, and
    /// the <c>ref SpatialTreeRoot</c> threading the design proposes buys performance (one shared <c>_metadataLock</c> and one chunk-0 write per insert), not
    /// correctness. That is a much smaller change with a much smaller blast radius.</para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void Claim2_TwoOwnedTreesShareOneSegmentCorrectly()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df32);
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 64, desc.Stride);

            var treeA = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);
            var treeB = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);

            var accessor = segment.CreateChunkAccessor();
            try
            {
                // Round-robin so the two trees' chunk ids interleave on every page — the layout a shared segment actually produces.
                var rng = new Random(31337);
                var inA = new List<long>();
                var inB = new List<long>();
                for (int i = 0; i < 120; i++)
                {
                    double x = rng.NextDouble() * 1000;
                    double y = rng.NextDouble() * 1000;
                    long id = 1000 + i;
                    if ((i & 1) == 0) { treeA.Insert(id, Box(desc, x, y, x + 4, y + 4), ref accessor); inA.Add(id); }
                    else { treeB.Insert(id, Box(desc, x, y, x + 4, y + 4), ref accessor); inB.Add(id); }
                }

                var whole = Box(desc, -1e6, -1e6, 1e6, 1e6);
                var fromA = new HashSet<long>();
                foreach (var r in treeA.QueryAABB(whole)) { fromA.Add(r.PayloadId); }
                var fromB = new HashSet<long>();
                foreach (var r in treeB.QueryAABB(whole)) { fromB.Add(r.PayloadId); }

                TestContext.Out.WriteLine($"CLAIM2 rootA={treeA.RootChunkId} rootB={treeB.RootChunkId} nodesA={treeA.NodeCount} nodesB={treeB.NodeCount} "
                    + $"depthA={treeA.Depth} depthB={treeB.Depth} insertedA={inA.Count} insertedB={inB.Count} foundA={fromA.Count} foundB={fromB.Count} "
                    + $"crossLeak={fromA.Overlaps(inB)}|{fromB.Overlaps(inA)}");

                Assert.Multiple(() =>
                {
                    Assert.That(fromA, Is.EquivalentTo(inA), "tree A must return exactly its own payloads");
                    Assert.That(fromB, Is.EquivalentTo(inB), "tree B must return exactly its own payloads");
                    Assert.That(treeA.Depth, Is.GreaterThan(1), "this fixture must actually split tree A, or it proves nothing");
                    Assert.That(treeB.Depth, Is.GreaterThan(1), "this fixture must actually split tree B, or it proves nothing");
                });
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>
    /// <b>Claim 3.</b> Node cost per tree at the sparse populations the VDB grid actually produces — the number <c>AC-8.5</c>'s reporting shape needs and
    /// which decides whether <c>C4</c>'s "no linear-scan fallback" is affordable in SPACE as well as in time.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void Claim3_NodeCostAtSparseCellPopulations()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df32);
            TestContext.Out.WriteLine($"CLAIM3 stride={desc.Stride} leafCapacity={desc.LeafCapacity} internalCapacity={desc.InternalCapacity}");

            foreach (int clusters in new[] { 1, 2, 4, 8, 16, 50, 500 })
            {
                var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 48, desc.Stride);
                var tree = new SpatialRTree<PersistentStore>(segment, SpatialVariant.R3Df32);
                var accessor = segment.CreateChunkAccessor();
                try
                {
                    var rng = new Random(4242 + clusters);
                    for (long id = 1; id <= clusters; id++)
                    {
                        double x = rng.NextDouble() * 100;
                        double y = rng.NextDouble() * 100;
                        tree.Insert(id, Box(desc, x, y, x + 1, y + 1), ref accessor);
                    }

                    long treeBytes = (long)tree.NodeCount * desc.Stride;
                    // Today's linear SoA for the same cell: ClusterIds + 6 bounds + CategoryMask = 32 B per cluster, over a doubling array from 16.
                    int soaCapacity = 16;
                    while (soaCapacity < clusters) { soaCapacity *= 2; }
                    long soaBytes = (long)soaCapacity * 32;

                    TestContext.Out.WriteLine($"CLAIM3 clusters={clusters,4} nodes={tree.NodeCount,3} depth={tree.Depth} "
                        + $"treeBytes={treeBytes,6} soaBytes={soaBytes,6} ratio={(double)treeBytes / soaBytes:F2}");

                    // This was Assert.Pass("reported") — an assertion no mutation of SpatialRTree could redden, in a fixture that (unlike its two sibling
                    // instruments) is not [Explicit], so it ran on every gate shard for no signal at all. Node count and depth are what the measurement is
                    // ABOUT, so they are what gets pinned: a tree cannot store more entries than its leaves have capacity for, and depth must grow with the
                    // population rather than staying flat. A tree that silently dropped inserts would fail the first; one that never split, the second.
                    Assert.Multiple(() =>
                    {
                        Assert.That(tree.NodeCount, Is.GreaterThanOrEqualTo((clusters + desc.LeafCapacity - 1) / desc.LeafCapacity),
                            $"{clusters} entries need at least ceil({clusters} / {desc.LeafCapacity}) leaves");
                        Assert.That(tree.Depth, Is.GreaterThanOrEqualTo(clusters > desc.LeafCapacity ? 2 : 1),
                            "a population past one leaf must have split, so depth must be at least 2");
                        Assert.That(tree.EntityCount, Is.EqualTo(clusters), "every insert must be present in the tree");
                    });
                }
                finally
                {
                    accessor.Dispose();
                }
            }

        }
        finally
        {
            guard.Dispose();
        }
    }
}
