using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// #804: the Migrate phase's width comes from the cost model, never from <c>MinMigrationSliceSize</c>. Drives
/// <see cref="FenceWorkPlan.Build"/> for <see cref="FencePhase.Migrate"/> over a seeded drain prefix — one destination cell per request, so no slice
/// grows to a cell boundary — and checks the plan against the cost grain it is supposed to follow.
/// </summary>
[NonParallelizable]   // FenceWorkPlanComputeMaxChunksTests mutates the planner statics this reads
[TestFixture]
class FenceWorkPlanMigrateSliceTests : TestBase<FenceWorkPlanMigrateSliceTests>
{
    private const int Oversubscription = 2;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static FenceWorkPlan BuildMigratePlan(DatabaseEngine dbe, int pending, float migrationCostUs, int workers)
    {
        var state = dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;
        var requests = new MigrationRequest[pending];
        for (var i = 0; i < pending; i++)
        {
            requests[i] = new MigrationRequest(sourceClusterChunkId: i, sourceSlotIndex: i & 63, destRealm: 0, destCellKey: i);
        }

        state.PendingMigrations = requests;
        state.PendingMigrationDrainCount = pending;

        var plan = new FenceWorkPlan();
        plan.Build(FencePhase.Migrate, dbe, new LiveFenceCostModel(new FenceCostModel(migrationCostUs, 2.4f, 1f, 1f)), workers, Oversubscription);
        return plan;
    }

    /// <summary>
    /// The issue's claim, as a property: the plan never dispatches more Migrate chunks, nor carves more slices, than the cost grain calls for, whatever
    /// per-migration cost the live model holds. The cases are the issue's own scenario (W = 30, 1 650 migrations) at its claimed cost (1.0 µs), at the
    /// chunk wall per migration its profiled run implies (2 718 ms over 200 ticks × 1 650 = 8.24 µs) and at the seed; then Matrix M's measured points
    /// (W = 8, adaptive, unprofiled); then the edges. Costs are in nanoseconds: a '.' in a case name makes an invalid database name.
    /// </summary>
    [TestCase(1650, 1000, 30)]
    [TestCase(1650, 8240, 30)]
    [TestCase(1650, 33300, 30)]
    [TestCase(4976, 1881, 8)]
    [TestCase(1223, 896, 8)]
    [TestCase(473, 1278, 8)]
    [TestCase(225, 1096, 8)]
    [TestCase(41, 1408, 8)]
    [TestCase(33, 1000, 8)]
    [TestCase(1, 500, 8)]
    [TestCase(100_000, 500, 8)]
    public void MigratePlan_NeverWiderThanTheCostGrain(int pending, int migrationCostNs, int workers)
    {
        var migrationCostUs = migrationCostNs / 1000f;
        using var dbe = SetupEngine();
        var plan = BuildMigratePlan(dbe, pending, migrationCostUs, workers);

        var total = migrationCostUs * pending;
        var grainChunks = FenceWorkPlan.ComputeMaxChunks(total, workers, Oversubscription);
        var units = 0;
        for (var i = 0; i < plan.ItemCount; i++)
        {
            Assert.That(plan.Items[i].Kind, Is.EqualTo(FenceWorkKind.MigrationApply));
            units += plan.Items[i].UnitCount;
        }

        TestContext.WriteLine($"P={pending} m={migrationCostUs} µs W={workers} O={Oversubscription} | total {total:F0} µs | grain chunks {grainChunks} | "
                              + $"slices {plan.ItemCount} | chunks {plan.ChunkCount} | {total / plan.ChunkCount:F1} µs/chunk");

        Assert.That(units, Is.EqualTo(pending), "every drained migration is in exactly one slice");
        Assert.That(plan.ItemCount, Is.LessThanOrEqualTo(grainChunks), "MinMigrationSliceSize can only remove slices the cost grain asked for, never add one");
        Assert.That(plan.ChunkCount, Is.LessThanOrEqualTo(grainChunks), "the Migrate phase is never wider than its cost grain");
    }
}
