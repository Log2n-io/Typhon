using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Directly drives FenceWorkPlan.Build with a real DatabaseEngine and AntHill-faithful ClusterProcessBitmap state.
/// No runtime, no parallel dispatch — just the plan emission + bin-packing path. Prints stats so we can SEE what
/// the chunk-count policy produces.
/// </summary>
[NonParallelizable]
[TestFixture]
class FenceChunkSizingDiagnosticTests : TestBase<FenceChunkSizingDiagnosticTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<ClMigUnit>.Touch();

    private DatabaseEngine SetupSpatialEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        dbe.SetSpatialBarrierOnly<ClMigUnit>();
        return dbe;
    }

    private static void SpawnSpread(DatabaseEngine dbe, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            float x = (i % 30) * 30f + 20f;
            float y = (i / 30) * 30f + 20f;
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(new ClMigPos
            {
                Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                Tag = i,
            }));
        }
        tx.Commit();
    }

    /// <summary>
    /// Take a snapshot of the AABB plan after directly seeding ClusterProcessBitmap with `dirtyClusterCount`
    /// dirty bits (consecutive chunkIds) and FenceBranchPath=1 (forces emitter past short-circuit). This
    /// faithfully reproduces what the emitter sees in BarrierOnly mode mid-tick, without running the runtime.
    /// </summary>
    private static (int items, int chunks, float totalCost, float maxAtomic, int bitmapWords) DriveBuild(
        DatabaseEngine dbe, int dirtyClusterCount, int workerCount, int oversubscription, float aabbCostUsPerCluster)
    {
        var meta = Archetype<ClMigUnit>.Metadata;
        var state = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Make sure the bitmap and per-cluster arrays are sized large enough for our dirty range.
        state.EnsureClusterWriteBookkeepingCapacity(dirtyClusterCount + 64);

        // Seed: mark `dirtyClusterCount` consecutive chunkIds as dirty.
        var bitmap = state.ClusterProcessBitmap;
        for (int i = 0; i < bitmap.Length; i++) bitmap[i] = 0;
        for (int c = 0; c < dirtyClusterCount; c++)
        {
            int wordIdx = c >> 6;
            int bitIdx = c & 63;
            bitmap[wordIdx] |= 1L << bitIdx;
        }
        state.FenceBranchPath = 1; // bypass the "Prep returned false" short-circuit
        state.FenceProcessBitmapClusterCount = dirtyClusterCount; // memoized popcount

        var seed = new FenceCostModel(MigrationCost: 33.3f, AabbCost: aabbCostUsPerCluster, ShadowCost: 1f, SpatialCost: 1f);
        var liveCost = new LiveFenceCostModel(seed);
        var plan = new FenceWorkPlan();
        plan.Build(FencePhase.AabbRefresh, dbe, liveCost, workerCount, oversubscription);

        float totalCost = 0f;
        float maxAtomic = 0f;
        for (int i = 0; i < plan.ItemCount; i++)
        {
            float c = plan.Items[i].Cost;
            totalCost += c;
            if (c > maxAtomic) maxAtomic = c;
        }

        return (plan.ItemCount, plan.ChunkCount, totalCost, maxAtomic, bitmap.Length);
    }

    [Test]
    public void AntHill_550Clusters_W16O2_Should_Reach_28_Chunks()
    {
        using var dbe = SetupSpatialEngine();
        SpawnSpread(dbe, 1100); // ensure cluster state has enough capacity

        // AntHill measured: 1100µs CPU at 2µs/cluster → 550 dirty clusters.
        var r = DriveBuild(dbe, dirtyClusterCount: 550, workerCount: 16, oversubscription: 2, aabbCostUsPerCluster: 2f);

        TestContext.WriteLine($"550 dirty clusters | W=16 O=2 | AabbCost=2µs/cluster");
        TestContext.WriteLine($"  bitmap.Length = {r.bitmapWords} words");
        TestContext.WriteLine($"  totalCost = {r.totalCost:F1} µs");
        TestContext.WriteLine($"  maxAtomicCost = {r.maxAtomic:F1} µs");
        TestContext.WriteLine($"  ItemCount = {r.items}");
        TestContext.WriteLine($"  ChunkCount = {r.chunks}");
        TestContext.WriteLine($"  predicted-per-chunk = {(r.chunks > 0 ? r.totalCost / r.chunks : 0):F1} µs");
        TestContext.WriteLine($"  TARGET: ~28 chunks at ~200µs each");
    }

    [Test]
    public void AntHill_550Clusters_W16O2_Hits_6_Chunks()
    {
        using var dbe = SetupSpatialEngine();
        SpawnSpread(dbe, 1100);

        var r = DriveBuild(dbe, 550, 16, 2, 2f);
        // After the 1-word-per-slice fix: 16 items (1 per word), ChunkCount = ceil(1100/200) = 6.
        Assert.That(r.chunks, Is.EqualTo(6), $"expected 6 chunks for 1100µs total at 200µs floor; got {r.chunks}");
        Assert.That(r.items, Is.GreaterThanOrEqualTo(r.chunks), "ItemCount must not bind ChunkCount");
        Assert.That(r.totalCost / r.chunks, Is.InRange(150f, 220f), "per-chunk cost should be ~200µs");
    }
}
