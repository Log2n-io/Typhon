using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Ray-query completeness when the traversal frontier outgrows the enumerator's inline priority queue (#589).
/// </summary>
/// <remarks>
/// <para>
/// <c>RayEnumerator</c> keeps a min-heap of pending nodes in a 64-entry inline buffer. Front-to-back popping means the frontier grows before it drains — one
/// internal node can push up to 24 children (2D-f32) — so three expansions exceed 64 in a dense scene. Until #589 the capacity check was folded into the push
/// condition with no else and no diagnostic: a child the ray hit within range was simply not enqueued and its whole subtree never visited, so the query
/// returned a silently incomplete answer (SQ-01).
/// </para>
/// <para>
/// <b>Why the boxes are large and overlapping.</b> A sparse scene gives tight node MBRs, so a ray crosses only a thin corridor of them and the frontier
/// never approaches 64 — which is exactly why <see cref="SpatialQueryTests"/>'s 200-entity ray test never caught this. Oversized overlapping AABBs make
/// almost every node MBR straddle the ray line, so nearly every child of every visited node is pushed and the frontier grows fast.
/// </para>
/// <para>
/// <b>On ordering.</b> The design describes ray results as front-to-back, but that holds per <i>node</i>, not per entity: once a leaf is popped its entries are
/// scanned in slot order and yielded as they hit. These tests therefore assert set equality, not sequence equality — asserting a global t-ordering the
/// implementation does not promise would be testing a fiction.
/// </para>
/// </remarks>
[TestFixture]
public class SpatialRayHeapTests
{
    private const int EntityCount = 6000;
    private const double WorldLength = 4000.0;   // extent along the ray's axis
    private const double RayLineCoord = 1000.0;  // the ray's fixed coordinate on every other axis

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
            return $"SRH{testName}";
        }
    }

    [SetUp]
    public void Setup()
    {
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialRayHeapTests");
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

    /// <summary>
    /// Build a dense scene of oversized, heavily overlapping AABBs — see the fixture remarks for why the overlap is load-bearing.
    /// </summary>
    private (SpatialRTree<PersistentStore> tree, BruteForceSpatialIndex oracle) BuildDenseOverlappingScene(ManagedPagedMMF pmmf, SpatialVariant variant,
        int seed = 4242)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 256, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var oracle = new BruteForceSpatialIndex(desc.CoordCount);
        var rng = new Random(seed);
        int halfCoord = desc.CoordCount / 2;

        var accessor = segment.CreateChunkAccessor();
        try
        {
            // Hoisted out of the loop: a stackalloc inside a loop is never released until the method returns (CA2014).
            Span<double> coords = stackalloc double[desc.CoordCount];
            for (int i = 0; i < EntityCount; i++)
            {
                // Every box spans nearly the whole ray axis, and varies only on the perpendicular axes — where it is always placed to straddle RayLineCoord.
                //
                // The perpendicular partitioning is the point. If the tree splits along the RAY's axis, front-to-back popping drains depth-first and the
                // frontier stays tiny: level-2 siblings get well-separated entry distances (~10, ~610, ~1210), so the leaves of the first sibling all sort
                // ahead of the second sibling and are consumed before it is ever expanded. That is why a ray query normally visits only 5-15 nodes. Forcing
                // the split onto the perpendicular axes instead gives every node the SAME entry distance, so siblings pile up in the heap unconsumed.
                for (int d = 0; d < halfCoord; d++)
                {
                    double size = d == 0 ? WorldLength - 200.0 : rng.NextDouble() * 200.0 + 200.0;
                    double lo = d == 0 ? rng.NextDouble() * 100.0 : RayLineCoord - size * rng.NextDouble();
                    coords[d] = lo;
                    coords[d + halfCoord] = lo + size;
                }

                tree.Insert(i + 1, coords, ref accessor);
                oracle.Insert(i + 1, coords);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (tree, oracle);
    }

    private static HashSet<long> RayIds(SpatialRTree<PersistentStore> tree, ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist)
    {
        var ids = new HashSet<long>();
        foreach (var hit in tree.QueryRay(origin, direction, maxDist))
        {
            ids.Add(hit.EntityId);
        }

        return ids;
    }

    private static HashSet<long> OracleRayIds(BruteForceSpatialIndex oracle, ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist)
    {
        var ids = new HashSet<long>();
        foreach (var (id, _) in oracle.QueryRay(origin, direction, maxDist))
        {
            ids.Add(id);
        }

        return ids;
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [TestCase(SpatialVariant.R3Df32)]
    [CancelAfter(15000)]
    public void QueryRay_DenseScene_ReturnsEveryHit_WhenFrontierExceedsInlineHeap(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(variant);
            var (tree, oracle) = BuildDenseOverlappingScene(pmmf, variant);
            int halfCoord = desc.CoordCount / 2;

            Span<double> origin = stackalloc double[halfCoord];
            Span<double> direction = stackalloc double[halfCoord];
            for (int d = 0; d < halfCoord; d++)
            {
                origin[d] = d == 0 ? -10.0 : RayLineCoord;
                direction[d] = d == 0 ? 1.0 : 0.0;
            }

            const double maxDist = WorldLength + 1000.0;
            long spillsBefore = Interlocked.Read(ref SpatialRTreeDiagnostics.RayHeapSpillCount);
            long ceilingBefore = Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount);
            var fromTree = RayIds(tree, origin, direction, maxDist);
            var fromOracle = OracleRayIds(oracle, origin, direction, maxDist);
            long spills = Interlocked.Read(ref SpatialRTreeDiagnostics.RayHeapSpillCount) - spillsBefore;
            long ceilingHits = Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount) - ceilingBefore;

            // Preconditions, asserted rather than assumed. Without a spill the frontier never passed the inline capacity and this fixture would be testing
            // nothing — which is exactly the state SpatialQueryTests.QueryRay_MatchesBruteForce is in at 200 sparse entities.
            Assert.That(fromOracle, Has.Count.GreaterThan(200), "precondition: the scene is too sparse to grow the traversal frontier past the inline heap");
            Assert.That(spills, Is.GreaterThan(0),
                $"precondition: the ray heap never spilled ({fromOracle.Count} hits), so the >64-frontier path was not exercised — make the scene denser");

            Assert.That(fromTree, Is.EquivalentTo(fromOracle),
                $"SQ-01: ray query dropped {fromOracle.Count - fromTree.Count} of {fromOracle.Count} hits — the traversal frontier outgrew the inline heap "
                + $"and whole subtrees were never visited ({variant})");

            // The post-spill ceiling means a degenerate or cyclic tree. A well-formed one must grow, never hit it.
            Assert.That(ceilingHits, Is.Zero, $"a well-formed tree reached MaxRayHeapCapacity after {spills} spills — the growth path is not keeping up");
        }
        finally
        {
            guard.Dispose();
        }
    }

    [Test]
    [TestCase(SpatialVariant.R2Df32)]
    [CancelAfter(15000)]
    public void QueryRay_AbandonedMidIteration_DoesNotCorruptLaterQueries(SpatialVariant variant)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        try
        {
            var desc = SpatialNodeDescriptor.ForVariant(variant);
            var (tree, oracle) = BuildDenseOverlappingScene(pmmf, variant);
            int halfCoord = desc.CoordCount / 2;

            Span<double> origin = stackalloc double[halfCoord];
            Span<double> direction = stackalloc double[halfCoord];
            for (int d = 0; d < halfCoord; d++)
            {
                origin[d] = d == 0 ? -10.0 : RayLineCoord;
                direction[d] = d == 0 ? 1.0 : 0.0;
            }

            const double maxDist = WorldLength + 1000.0;

            // Abandon several enumerations partway through. Each one has grown (and must return) its rented spill buffer via foreach's finally; a buffer
            // returned twice, or handed back while still referenced, would surface as corrupted results on the next query.
            for (int round = 0; round < 20; round++)
            {
                int seen = 0;
                foreach (var hit in tree.QueryRay(origin, direction, maxDist))
                {
                    _ = hit.EntityId;
                    if (++seen == 5 + round)
                    {
                        break;
                    }
                }
            }

            var fromTree = RayIds(tree, origin, direction, maxDist);
            var fromOracle = OracleRayIds(oracle, origin, direction, maxDist);
            Assert.That(fromTree, Is.EquivalentTo(fromOracle), "a query following abandoned enumerations must still be complete");
        }
        finally
        {
            guard.Dispose();
        }
    }
}
