using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms D2 (RT-4, decision D-6): repair runs in every realm through the archetype's one queue, keyed by (realm, cell); a realm that is not runnable keeps
/// its candidates and gets no repair until it runs again — dormancy freezes elective work.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmRepairTests : TestBase<RealmRepairTests>
{
    private const float CellSize = 100f;
    private const int Cells = 3;
    private const int PerCell = 250;

    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000, 1000), CellSize,
        clusterTargetExtentRatio: 100f, clusterRepairExtentRatio: 0.75f, reclusterBudgetMs: 5f, batchSpawnSortThreshold: 0, repairWorstClustersPerUnit: 8,
        clusterRepairCriticalExtentRatio: 1.0f, clusterTargetPackingSlack: 0f);

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Realm 0 (the primary, empty), realm 1 an interior that sleeps after one unobserved tick, realm 2 simulated always — same geometry.</summary>
    private DatabaseEngine Engine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.Realms.Register(new RealmId(1),
            new RealmConfig { Grid = Grid(), WhenUnobserved = RealmUnobserved.Sleep, UnobservedTickDivisor = 1, SleepAfterTicks = 1 });
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid()));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Cells laid out in spawn order, so every cluster spans its whole cell — degraded past the repair ratio from birth.</summary>
    private static void SpawnDegraded(DatabaseEngine dbe, ushort realm)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var c = 0; c < Cells; c++)
        {
            for (var i = 0; i < PerCell; i++)
            {
                var x = (c * CellSize) + 4f + ((i * 37) % 92);
                var y = 4f + ((i * 61) % 92);
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos
                {
                    Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = i,
                }));
            }
        }

        tx.Commit();
    }

    /// <summary>The mean largest axis extent of the realm's clusters over its degraded cells.</summary>
    private static double MeanExtent(DatabaseEngine dbe, ushort realm)
    {
        var state = StateOf(dbe);
        var rs = state.RealmSpatial[realm];
        var total = 0d;
        var counted = 0;
        for (var c = 0; c < Cells; c++)
        {
            var clusters = rs.CellClusterPool.GetClusters(rs.Grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f));
            foreach (var chunkId in clusters)
            {
                ref var box = ref state.ClusterAabbs[chunkId];
                if (!float.IsPositiveInfinity(box.MinX))
                {
                    total += MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
                    counted++;
                }
            }
        }

        return counted == 0 ? 0d : total / counted;
    }

    /// <summary>One write per tick so the fence always has work — the planner runs from Prep.</summary>
    private static void Tick(DatabaseEngine dbe, long tick)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    if (cluster.OccupancyBits != 0 && cluster.Realm.Value == 2)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(cluster.OccupancyBits);
                        cluster.WriteSpatial(RealmUnit.Pos, slot, cluster.GetReadOnlySpan(RealmUnit.Pos)[slot]);
                        break;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        dbe.WriteTickFence(tick);
    }

    [Test]
    [VerifiesRule("RP-08")]
    [VerifiesRule("DM-04")]
    public void RepairRunsInEveryRealm_AndADormantRealmsCandidatesWait()
    {
        using var dbe = Engine();
        var realms = dbe.RealmTable;
        realms.EvaluatePolicy();
        realms.EvaluatePolicy();
        Assert.That(realms.StateOf(1), Is.EqualTo(RealmRunState.Dormant), "precondition: realm 1 sleeps");

        SpawnDegraded(dbe, 1);
        SpawnDegraded(dbe, 2);
        dbe.WriteTickFence(1);
        var before1 = MeanExtent(dbe, 1);
        var before2 = MeanExtent(dbe, 2);
        Assert.That((before1, before2), Is.EqualTo((before2, before1)).Within(1e-9), "precondition: the two realms hold the same layout");

        for (var t = 2; t <= 20; t++)
        {
            Tick(dbe, t);
        }

        var state = StateOf(dbe);
        Assert.That(MeanExtent(dbe, 2), Is.LessThan(before2 * 0.8), "a realm that is not the primary gets repaired (Realms D2)");
        Assert.That(MeanExtent(dbe, 1), Is.EqualTo(before1).Within(1e-9), "a dormant realm gets no repair");
        var waiting = 0;
        for (var c = 0; c < Cells; c++)
        {
            var key = CellRepairQueue.Key(1, state.RealmSpatial[1].Grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f));
            waiting += state.RepairQueue.DegradationOf(key) > 0f ? 1 : 0;
        }

        Assert.That(waiting, Is.EqualTo(Cells), "its candidates wait in the queue");

        // Woken, it is repaired from the candidates that waited — nothing re-nominates an untouched cell.
        realms.RequestWake(1);
        realms.EvaluatePolicy();
        for (var t = 21; t <= 40; t++)
        {
            Tick(dbe, t);
        }

        Assert.That(MeanExtent(dbe, 1), Is.LessThan(before1 * 0.8), "the waiting candidates are served once the realm runs");
    }
}
