using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The tick fence's IndexMassUpdate phase — the one #872 step 6 adds between Migrate and AabbRefresh (§5.5).
/// </summary>
/// <remarks>
/// The phase's <i>result</i> is covered in <c>ClusterMigrationTests</c> by
/// <c>ClusterIndex_MigrationUnderTheParallelFence_RepointsEveryMigrantsIndexValue</c>, which drives a live <c>TyphonRuntime</c> tick and asserts the index
/// against cluster occupancy. That test exists because the earlier claim — that every migration test covers this path end to end — was false: they all call
/// <c>WriteTickFence</c>, which is the SERIAL drain, so ablating this phase's plan emission left every one of them green. What is left for THIS fixture is
/// the phase's own contract: that it is wired into the DAG where the design says, and that it costs nothing on the overwhelmingly common tick where no
/// entity migrated.
/// </remarks>
[TestFixture]
class IndexMassUpdatePhaseTests : TestBase<IndexMassUpdatePhaseTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    [CancelAfter(15_000)]
    public void EmptyBatch_EmitsNoWorkItems()
    {
        // AC-6.7. A tick with no migration stages nothing, so the phase must produce no work items and therefore no chunks — the scheduler then skips the
        // dispatch entirely rather than waking workers to find nothing. This is the state the phase is in on almost every tick, so "skipped when empty" is
        // not an edge case, it is the common path.
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0)));
            }

            tx.Commit();
        }

        var plan = new FenceWorkPlan();
        var costModel = new LiveFenceCostModel(new FenceCostModel(MigrationCost: 33.3f, AabbCost: 1f, ShadowCost: 1f, SpatialCost: 1f));
        plan.Build(FencePhase.IndexMassUpdate, dbe, costModel, workerCount: 8, chunkOversubscription: 2);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ItemCount, Is.Zero, "nothing migrated, so nothing is staged and no index slice may be emitted");
            Assert.That(plan.ChunkCount, Is.Zero, "an empty plan must produce no chunks at all, not one empty chunk");
        });
    }

    [Test]
    [CancelAfter(15_000)]
    public void PhaseIsDeclaredBetweenMigrateAndAabbRefresh()
    {
        // §5.5's phase ordering, with a barrier either side:
        //
        //   Re-cluster (Migrate)   move component bytes, emit (key, oldValue, newValue)
        //       -- barrier --
        //   IndexMassUpdate        parallel, exclusive
        //       -- barrier --
        //
        // Both barriers carry weight. It cannot start before Migrate has staged the entries; and until it has finished, every migrated entity's index entry
        // still names the cluster location it left, so nothing downstream may read the index.
        using var dbe = SetupEngine();

        var names = new System.Collections.Generic.List<string>();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => { });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        for (var i = 0; i < runtime.Scheduler.AllSystemCount; i++)
        {
            names.Add(runtime.Scheduler.Systems[i].Name);
        }

        Assert.That(names, Does.Contain(FenceIndexMassUpdateExecSystem.SystemName),
            "the phase must be declared on the fence DAG, or migrations stage entries nothing ever applies");
    }
}
