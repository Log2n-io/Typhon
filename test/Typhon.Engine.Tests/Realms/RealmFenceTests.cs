using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C1c: the tick fence runs every realm's spatial maintenance in that realm's grid — crossing detection, the migration drain, the AABB refresh,
/// the finalization drain and the reach. The same population moves identically in three realms (two sharing a cell geometry, one not) through the
/// serial fence and the parallel one; afterwards each realm must hold exactly its own entities, filed in its own cells, and answer its queries like a
/// brute force over the positions written.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmFenceTests : TestBase<RealmFenceTests>
{
    private const float World = 100f;
    private const int PerRealm = 120;
    private const int RealmCount = 3;
    private const int Rounds = 3;
    private const int SerialArm = 0;

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), cellSize);

    private DatabaseEngine ThreeRealms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Grid(10)));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Round <paramref name="round"/>'s box for entity <paramref name="tag"/>: a pure function, the same in every realm.</summary>
    private static AABB2F BoxOf(int round, int tag)
    {
        var rng = new Random(unchecked(round * 7919 + tag * 104729));
        var x = (float)(rng.NextDouble() * (World - 2) + 1);
        var y = (float)(rng.NextDouble() * (World - 2) + 1);
        var half = (float)(rng.NextDouble() * 0.4);
        return new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
    }

    private static void SpawnMirrored(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var tag = 0; tag < PerRealm; tag++)
        {
            var b = BoxOf(0, tag);
            for (ushort r = 0; r < RealmCount; r++)
            {
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos { Bounds = b, Realm = r, Tag = tag }));
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Every entity to its round-<paramref name="round"/> box, keeping its realm key and tag — through <c>WriteSpatial</c> (the write barrier flags the
    /// crossing, drained per realm) or through <c>OpenMut</c> (no flag: the fence's dirty-bit scan detects it, per cluster realm).
    /// </summary>
    private static void MoveAll(DatabaseEngine dbe, int round, bool viaOpenMut)
    {
        if (viaOpenMut)
        {
            var moves = new List<(EntityId id, ushort realm, int tag)>();
            using (var rtx = dbe.CreateQuickTransaction())
            {
                var reader = rtx.For<RealmUnit>();
                try
                {
                    foreach (var cluster in reader.GetClusterEnumerator())
                    {
                        var occupied = cluster.OccupancyBits;
                        while (occupied != 0)
                        {
                            var slot = BitOperations.TrailingZeroCount(occupied);
                            occupied &= occupied - 1;
                            var v = cluster.GetReadOnly(RealmUnit.Pos, slot);
                            moves.Add((cluster.GetEntityId(slot), v.Realm, v.Tag));
                        }
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }

            using var wtx = dbe.CreateQuickTransaction();
            foreach (var (id, realm, tag) in moves)
            {
                ref var pos = ref wtx.OpenMut(id).Write(RealmUnit.Pos);
                pos = new RealmPos { Bounds = BoxOf(round, tag), Realm = realm, Tag = tag };
            }

            wtx.Commit();
            return;
        }

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<RealmUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var current = cluster.GetReadOnly(RealmUnit.Pos, slot);
                    cluster.WriteSpatial(RealmUnit.Pos, slot, current with { Bounds = BoxOf(round, current.Tag) });
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static void RunFences(DatabaseEngine dbe, int workers, ref long tick)
    {
        if (workers == SerialArm)
        {
            // Two fences: the first detects and drains, the second catches what the first's AABB refresh filed.
            dbe.WriteTickFence(++tick);
            dbe.WriteTickFence(++tick);
            return;
        }

        var ticks = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Count", _ => Interlocked.Increment(ref ticks));
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 100, EnableParallelFence = true });
        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 4 && runtime.CurrentTickNumber >= 4, TimeSpan.FromSeconds(15));
        runtime.Shutdown();
        Assert.That(unhandled, Is.Null, $"the parallel fence threw: {unhandled}");
        Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(4), "the runtime must have ticked");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("SQ-08")]
    public void MovingPopulations_StayInTheirRealms_FiledInTheirOwnCells([Values(SerialArm, 2, 8)] int workers, [Values] bool viaOpenMut)
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        for (var round = 1; round <= Rounds; round++)
        {
            MoveAll(dbe, round, viaOpenMut);
            RunFences(dbe, workers, ref tick);
            AssertRealmsConsistent(dbe, round, $"round {round}, {(workers == SerialArm ? "serial" : $"W={workers}")}, {(viaOpenMut ? "OpenMut" : "WriteSpatial")}");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void DestroyedClusters_AreFinalisedInTheirRealm_AndRecycledIdsDoNotLeak()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);

        // Empty realm 2 entirely: every one of its clusters drains and is freed through the finalization drain, in realm 2's cell state.
        var cs = StateOf(dbe);
        var realm2 = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (cluster.GetReadOnly(RealmUnit.Pos, slot).Realm == 2)
                        {
                            realm2.Add(cluster.GetEntityId(slot));
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(realm2, Has.Count.EqualTo(PerRealm));
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in realm2)
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++tick);
        var grid2 = dbe.RealmTable.Get(2).Grid;
        for (var cell = 0; cell < grid2.CellCount; cell++)
        {
            Assert.That(grid2.GetCell(cell).EntityCount, Is.Zero, $"realm 2 cell {cell} still counts an entity");
            Assert.That(cs.RealmSpatial[2].CellClusterPool.GetClusters(cell).Length, Is.Zero, $"realm 2 cell {cell} still lists a cluster");
        }

        // Spawn a second population into realm 0: freed realm-2 chunk ids are recycled into realm 0's cells, whose keys overlap realm 2's.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var tag = 0; tag < PerRealm; tag++)
            {
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos { Bounds = BoxOf(1, tag), Realm = 0, Tag = PerRealm + tag }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++tick);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var hits = 0;
        foreach (var _ in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(2)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }))
        {
            hits++;
        }

        Assert.That(hits, Is.Zero, "realm 2 is empty: a recycled chunk id must not answer for it");
        Assert.That(dbe.ClusterSpatialQuery<RealmUnit>(RealmId.Default).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }).Count(),
            Is.EqualTo(2 * PerRealm));
    }

    /// <summary>
    /// The realm invariants after the fences have drained a round: every cluster's entities carry its realm's key and sit in its cell of its realm's grid
    /// (CC-02, within the hysteresis dead zone); every cell's entity count is its realm's; each realm's reach covers its index; and every query answers
    /// exactly the brute force over the positions written.
    /// </summary>
    private static void AssertRealmsConsistent(DatabaseEngine dbe, int round, string context)
    {
        var cs = StateOf(dbe);
        var perRealmCellCounts = new Dictionary<(int realm, int cell), int>();
        var seen = new int[RealmCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var chunk = cluster.ChunkId;
                    var realm = cs.ClusterRealmMap[chunk];
                    var cellKey = cs.ClusterCellMap[chunk];
                    var grid = dbe.RealmTable.Get(realm).Grid;
                    var margin = grid.Config.CellSize * grid.Config.MigrationHysteresisRatio;
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var v = cluster.GetReadOnly(RealmUnit.Pos, slot);
                        Assert.That(v.Realm, Is.EqualTo(realm), $"an entity of realm {v.Realm} sits in a realm-{realm} cluster, {context}");
                        var cx = (v.Bounds.MinX + v.Bounds.MaxX) * 0.5;
                        var cy = (v.Bounds.MinY + v.Bounds.MaxY) * 0.5;
                        grid.CellOrigin(cellKey, out var ox, out var oy, out _);
                        var inCell = cx >= ox - margin && cx <= ox + grid.Config.CellSize + margin && cy >= oy - margin && cy <= oy + grid.Config.CellSize + margin;
                        Assert.That(inCell, Is.True, $"entity {v.Tag} of realm {realm} at ({cx}, {cy}) is filed in cell {cellKey} at ({ox}, {oy}), {context}");
                        perRealmCellCounts[(realm, cellKey)] = perRealmCellCounts.GetValueOrDefault((realm, cellKey)) + 1;
                        seen[realm]++;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        for (var r = 0; r < RealmCount; r++)
        {
            Assert.That(seen[r], Is.EqualTo(PerRealm), $"realm {r} holds {seen[r]} entities, {context}");
            var grid = dbe.RealmTable.Get((ushort)r).Grid;
            for (var cell = 0; cell < grid.CellCount; cell++)
            {
                Assert.That(grid.GetCell(cell).EntityCount, Is.EqualTo(perRealmCellCounts.GetValueOrDefault((r, cell))),
                    $"realm {r} cell {cell}'s entity count, {context}");
            }
        }

        Assert.That(cs.ReachCoversIndex(out var violation), Is.True, $"{violation}, {context}");

        var rng = new Random(round);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (var q = 0; q < 20; q++)
        {
            var cx = rng.NextDouble() * World;
            var cy = rng.NextDouble() * World;
            var ext = rng.NextDouble() * 20 + 1;
            var query = new AABB2F { MinX = (float)(cx - ext), MinY = (float)(cy - ext), MaxX = (float)(cx + ext), MaxY = (float)(cy + ext) };
            var expected = new HashSet<int>();
            for (var tag = 0; tag < PerRealm; tag++)
            {
                var b = BoxOf(round, tag);
                if (b.MinX <= query.MaxX && b.MaxX >= query.MinX && b.MinY <= query.MaxY && b.MaxY >= query.MinY)
                {
                    expected.Add(tag);
                }
            }

            for (ushort r = 0; r < RealmCount; r++)
            {
                var got = new HashSet<int>();
                foreach (var hit in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(r)).AABB(query))
                {
                    Assert.That(cs.ClusterRealmMap[hit.ClusterChunkId], Is.EqualTo(r), $"query {q} in realm {r} answered another realm's cluster, {context}");
                    Assert.That(got.Add(TagOf(dbe, hit.Entity)), Is.True, $"duplicate hit, query {q}, realm {r}, {context}");
                }

                Assert.That(got, Is.EquivalentTo(expected), $"query {q} in realm {r}, {context}");
            }
        }
    }

    private static int TagOf(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Open(id).Read(RealmUnit.Pos).Tag;
    }
}
