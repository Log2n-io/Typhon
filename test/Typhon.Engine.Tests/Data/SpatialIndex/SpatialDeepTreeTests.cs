using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Structural and query correctness at tree depth ≥ 3 — the regime <see cref="SpatialRTreeTests"/> (100 entities) and <see cref="SpatialQueryTests"/>
/// (200 entities) never reach.
/// </summary>
/// <remarks>
/// <para>
/// Depth matters because <c>PropagateSplit</c>'s "refit remaining ancestors" loop only has a body to execute when the descent path is at least two levels
/// deep — i.e. above a leaf's parent. Below ~LeafCapacity × InternalCapacity entities the loop is dead code, which is why #588 (ancestors refit from
/// pre-insert entry coords → MBRs too tight → queries silently prune overlapping subtrees, ST-01) survived both review and a green suite.
/// </para>
/// <para>
/// <b>Two design choices here are load-bearing; neither is stylistic.</b>
/// </para>
/// <para>
/// <b>1. The world extent grows with every insert.</b> Inserting uniformly into a <i>fixed</i> world does not reliably reproduce a stale-ancestor bug: the
/// upper nodes saturate to the full extent within the first few hundred entities, after which a split barely enlarges any ancestor and the staleness error
/// decays toward the comparison epsilon. Growing the world — and pinning every fourth entity to the frontier — makes every split enlarge the ancestors
/// above it, so a missing refresh is always a measurable gap rather than a rounding artifact.
/// </para>
/// <para>
/// <b>2. Assertions run at checkpoints DURING the build, never only at the end.</b> The damage is transient: a later insert descending the same path calls
/// <c>RefitAncestors</c>, which does refresh the entry and thereby heals it. A tree built to 1500 entities and only then validated comes back clean even with
/// the bug fully present — measured, not assumed. The exposure in production is the same window seen from the other side: a read issued before the next write
/// touches that path silently misses entities, and if inserts stop there (bulk-load then query — the common shape) the window never closes.
/// </para>
/// <para>
/// The two checks are deliberately different in kind. <see cref="TreeValidator.Validate{TStore}"/> is the deterministic oracle — a stale entry throws
/// wherever it is. The self-query completeness sweep is the user-visible consequence, and it is the *discriminating* query shape: a whole-world box overlaps a
/// too-tight ancestor MBR and descends into it anyway, so only a box narrow enough to sit inside the gap is pruned. Random probing will not find an 8-unit
/// hole in a 3000-unit world; querying each entity by its own bounds finds it every time.
/// </para>
/// </remarks>
[TestFixture]
public unsafe class SpatialDeepTreeTests
{
    private const int EntityCount = 1500;
    private const int ValidateEvery = 100;
    private const int QuerySweepEvery = 300;

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
            if (testName.Length > 30)
            {
                testName = testName[^30..];
            }
            return $"SDT{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialDeepTreeTests");
        Directory.CreateDirectory(_testDatabaseDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "mm:ss.fff "; });
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseDirectory = _testDatabaseDir;
                o.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        if (_testDatabaseDir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_testDatabaseDir))
                {
                    try { File.Delete(file); }
                    catch { /* ignore cleanup failures */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static double WorldExtentAt(int i) => 100.0 + i * 2.0;

    /// <summary>
    /// Pre-generate every entity's AABB in a world whose extent grows with each one. Every fourth entity is pinned to the far corner so the outer boundary is
    /// strictly monotonic; the rest are scattered so the tree still has to make real grouping decisions. Generated up front (not inside the insert loop) so a
    /// checkpoint callback can re-query an entity by its own bounds.
    /// </summary>
    private static double[][] GenerateCoords(int coordCount, int seed)
    {
        var rng = new Random(seed);
        int halfCoord = coordCount / 2;
        var all = new double[EntityCount][];

        for (int i = 0; i < EntityCount; i++)
        {
            var coords = new double[coordCount];
            double extent = WorldExtentAt(i);
            bool frontier = i % 4 == 0;

            for (int d = 0; d < halfCoord; d++)
            {
                double size = rng.NextDouble() * 5.0 + 1.0;
                double v = frontier ? extent - size : rng.NextDouble() * (extent - size);
                coords[d] = v;
                coords[d + halfCoord] = v + size;
            }

            all[i] = coords;
        }

        return all;
    }

    /// <summary>Walk the leftmost spine to measure tree depth (1 == a single leaf root).</summary>
    private static int ComputeDepth(SpatialRTree<PersistentStore> tree, ref ChunkAccessor<PersistentStore> accessor)
    {
        var desc = tree.Descriptor;
        int depth = 1;
        int chunkId = tree.RootChunkId;

        while (true)
        {
            byte* nodeBase = accessor.GetChunkAddress(chunkId);
            if (SpatialNodeHelper.IsLeaf(nodeBase) || SpatialNodeHelper.GetCount(nodeBase) == 0)
            {
                return depth;
            }

            chunkId = SpatialNodeHelper.ReadInternalChildId(nodeBase, 0, desc);
            depth++;
        }
    }

    /// <summary>
    /// Insert every entity, invoking <paramref name="onCheckpoint"/> with (tree, insertedCount) every <paramref name="checkpointEvery"/> inserts and once at
    /// the end. Omit both to just build the tree.
    /// </summary>
    private (SpatialRTree<PersistentStore> tree, int depth) BuildDeepTree(ManagedPagedMMF pmmf, SpatialVariant variant, double[][] allCoords,
        ref ChunkAccessor<PersistentStore> accessor, int checkpointEvery = int.MaxValue, Action<SpatialRTree<PersistentStore>, int> onCheckpoint = null)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 256, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);

        accessor = segment.CreateChunkAccessor();

        for (int i = 0; i < EntityCount; i++)
        {
            tree.Insert(i + 1, allCoords[i], ref accessor);

            if ((i + 1) % checkpointEvery == 0)
            {
                onCheckpoint?.Invoke(tree, i + 1);
            }
        }

        onCheckpoint?.Invoke(tree, EntityCount);
        return (tree, ComputeDepth(tree, ref accessor));
    }

    /// <summary>Does a query over <paramref name="queryCoords"/> reach <paramref name="entityId"/>? Stops at the first hit — no list materialized.</summary>
    private static bool QueryFinds(SpatialRTree<PersistentStore> tree, ReadOnlySpan<double> queryCoords, long entityId)
    {
        foreach (var result in tree.QueryAABB(queryCoords))
        {
            if (result.EntityId == entityId)
            {
                return true;
            }
        }

        return false;
    }

    private static List<long> CollectQueryResults(SpatialRTree<PersistentStore> tree, ReadOnlySpan<double> queryCoords)
    {
        var results = new List<long>();
        foreach (var result in tree.QueryAABB(queryCoords))
        {
            results.Add(result.EntityId);
        }
        results.Sort();
        return results;
    }

    // ── AC2: the tree gets deep, and every intermediate state is structurally sound ───────────────────────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [CancelAfter(15000)]
    public void DeepTree_StaysStructurallyValid_ThroughAncestorRefits(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        var accessor = default(ChunkAccessor<PersistentStore>);

        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(variant);
            var coords = GenerateCoords(desc.CoordCount, seed: 1337);

            var (_, depth) = BuildDeepTree(pmmf, variant, coords, ref accessor, ValidateEvery, static (t, _) => TreeValidator.Validate(t));

            // Precondition, asserted rather than assumed: capacities are derived from the descriptor's stride, so a layout change could silently drop this
            // fixture back below the depth where PropagateSplit's ancestor loop has a body — making every assertion here vacuous.
            Assert.That(depth, Is.GreaterThanOrEqualTo(3),
                $"precondition: tree depth is {depth}, so PropagateSplit's ancestor loop never ran and this fixture proves nothing — raise EntityCount");
        }
        finally
        {
            accessor.Dispose();
            guard.Dispose();
        }
    }

    // ── AC3: queries at depth ≥ 3 agree with brute force (SQ-01 completeness, SQ-03 count consistency) ────

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [CancelAfter(15000)]
    public void DeepTree_EveryEntityStaysReachable_ByItsOwnBounds(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        var accessor = default(ChunkAccessor<PersistentStore>);

        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(variant);
            var coords = GenerateCoords(desc.CoordCount, seed: 1337);

            var (_, depth) = BuildDeepTree(pmmf, variant, coords, ref accessor, QuerySweepEvery, (tree, inserted) =>
            {
                var unreachable = new List<long>();
                for (int i = 0; i < inserted; i++)
                {
                    if (!QueryFinds(tree, coords[i], i + 1))
                    {
                        unreachable.Add(i + 1);
                    }
                }

                Assert.That(unreachable, Is.Empty,
                    $"SQ-01/ST-01: after {inserted} inserts, {unreachable.Count} entities are unreachable by a query on their own bounds — an ancestor MBR "
                    + $"is too tight ({variant}). First few: {string.Join(", ", unreachable.GetRange(0, Math.Min(8, unreachable.Count)))}");
            });

            Assert.That(depth, Is.GreaterThanOrEqualTo(3), "precondition: tree is not deep enough to exercise the ancestor refit path");
        }
        finally
        {
            accessor.Dispose();
            guard.Dispose();
        }
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [CancelAfter(15000)]
    public void DeepTree_QueryAABB_MatchesBruteForce(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);
        var accessor = default(ChunkAccessor<PersistentStore>);

        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(variant);
            int halfCoord = desc.CoordCount / 2;
            var coords = GenerateCoords(desc.CoordCount, seed: 1337);

            var oracle = new BruteForceSpatialIndex(desc.CoordCount);
            for (int i = 0; i < EntityCount; i++)
            {
                oracle.Insert(i + 1, coords[i]);
            }

            var (tree, depth) = BuildDeepTree(pmmf, variant, coords, ref accessor);
            Assert.That(depth, Is.GreaterThanOrEqualTo(3), "precondition: tree is not deep enough to exercise the ancestor refit path");

            double worldExtent = WorldExtentAt(EntityCount - 1);

            // Whole world. A weak check for #588 on its own — a box spanning everything still overlaps a too-tight ancestor MBR, so the descent reaches the
            // child anyway — but it is the floor below which nothing else is worth asserting.
            Span<double> whole = stackalloc double[desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                whole[d] = -1.0;
                whole[d + halfCoord] = worldExtent + 1.0;
            }
            Assert.That(CollectQueryResults(tree, whole), Has.Count.EqualTo(EntityCount), "SQ-01: full-extent query must return every entity");

            // Boxes swept along the frontier, plus scattered ones — differential against brute force.
            var rng = new Random(20260731);
            Span<double> box = stackalloc double[desc.CoordCount];
            for (int q = 0; q < 200; q++)
            {
                bool nearFrontier = q % 2 == 0;
                for (int d = 0; d < halfCoord; d++)
                {
                    double lo = nearFrontier ? worldExtent * (0.75 + rng.NextDouble() * 0.25) : rng.NextDouble() * worldExtent;
                    double span = rng.NextDouble() * (worldExtent * 0.15) + 1.0;
                    box[d] = lo;
                    box[d + halfCoord] = lo + span;
                }

                var fromTree = CollectQueryResults(tree, box);
                var fromOracle = oracle.QueryAABB(box);
                fromOracle.Sort();

                Assert.That(fromTree, Is.EqualTo(fromOracle), $"SQ-01: query {q} disagrees with brute force (depth {depth}, {variant})");

                // SQ-03: the count shortcut must agree with the materialized enumeration.
                Assert.That(tree.CountInAABB(box), Is.EqualTo(fromTree.Count), $"SQ-03: CountInAABB disagrees with QueryAABB for query {q}");
            }
        }
        finally
        {
            accessor.Dispose();
            guard.Dispose();
        }
    }
}
