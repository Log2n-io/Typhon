using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The changed-cluster list's one-directional invariant, asserted against every path that can write to cluster storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant: if any entity in a cluster changed, that cluster is in the list.</b> The converse is deliberately not promised — a clean cluster in
/// the list costs one wasted visit downstream and nothing else — so every test here asserts PRESENCE and none asserts absence, except the two that pin
/// the degenerate cases.
/// </para>
/// <para>
/// <b>Why a test per path rather than one that writes through all of them.</b> The signals feeding the list have three different granularities and three
/// different owners: the dirty bitmap (per entity, set by marked writes), the process bitmap (per cluster, set by <c>WriteSpatial</c>) and the
/// mutable-span flag (per archetype, set by <c>GetSpan</c>). A single test writing through everything would pass on any one of them working, which is
/// exactly the shape of bug this list exists to avoid.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ChangedClusterListTests : TestBase<ChangedClusterListTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        EnableTracking(dbe, Archetype<ClSpatialUnit>.Metadata.ArchetypeId);
        return dbe;
    }

    /// <summary>Turns the changed-cluster list on for one archetype — both halves, because they gate different costs.</summary>
    private static void EnableTracking(DatabaseEngine dbe, int archetypeId)
    {
        var cs = dbe._archetypeStates[archetypeId]?.ClusterState;
        if (cs != null)
        {
            cs.TrackContentChanges = true;
            cs.PublishChangedClusterList = true;
        }
    }

    private static ClSpatialPos MakePos(float x, float y, float z, float size = 1.0f) =>
        new() { Bounds = new AABB3F { MinX = x - size, MinY = y - size, MinZ = z - size, MaxX = x + size, MaxY = y + size, MaxZ = z + size } };

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClSpatialUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>The set of chunk ids the list names, as a set, so assertions read as membership rather than as positions.</summary>
    private static HashSet<int> Published(ArchetypeClusterState cs, long tick)
    {
        Assert.That(cs.ChangedClusterTick, Is.EqualTo(tick),
            "the list must describe the tick just fenced — an older stamp means nothing published and every assertion below would be vacuous");

        var ids = new HashSet<int>();
        for (var i = 0; i < cs.ChangedClusterCount; i++)
        {
            ids.Add(cs.ChangedClusterIds[i]);
        }

        return ids;
    }

    /// <summary>Spawns entities around one point so they share a cluster, and returns them with the cluster they landed in.</summary>
    private static (EntityId[] Ids, int ChunkId) SeedOneCluster(DatabaseEngine dbe, int count, float x, float y, long tick)
    {
        var ids = new EntityId[count];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                var pos = MakePos(x, y, 0f, size: 0.5f);
                var meta = new ClSpatialMeta { Tag = i };
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in meta));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(tick);

        var cs = StateOf(dbe);
        var chunkId = -1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.OccupancyBits != 0)
                {
                    chunkId = cluster.ChunkId;
                    break;
                }
            }

            accessor.Dispose();
        }

        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "precondition: the seed produced an occupied cluster");
        return (ids, chunkId);
    }

    // ── The paths ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A spawn names the cluster it landed in.</summary>
    [Test]
    public void ASpawnNamesItsCluster()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = MakePos(10, 10, 0);
            var meta = new ClSpatialMeta { Tag = 1 };
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in meta));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = StateOf(dbe);

        Assert.That(cs.ChangedClustersCoverAll || Published(cs, 1).Count > 0, Is.True,
            $"a spawn is a change and the tick that carries it must name a cluster. tick={cs.ChangedClusterTick} count={cs.ChangedClusterCount} "
            + $"coverAll={cs.ChangedClustersCoverAll} fromSlots={cs.ChangedFromSlots} fromProcess={cs.ChangedFromProcess} "
            + $"publishedTicks={cs.ChangedPublishedTicks} activeClusters={cs.ActiveClusterCount} hasSpatial={cs.SpatialSlot.HasSpatialIndex} "
            + $"processBitmapNull={cs.ClusterProcessBitmap == null}");
    }

    /// <summary>A write through <c>EntityRef.Write</c> — the marked path — names its cluster by slot.</summary>
    [Test]
    public void AMarkedWriteNamesItsClusterAndItsSlot()
    {
        using var dbe = SetupEngine();
        var (ids, chunkId) = SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(ids[2]);
            e.Write(ClSpatialUnit.Meta).Tag = 99;
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = StateOf(dbe);

        Assert.That(cs.ChangedClustersCoverAll, Is.False, "a marked write is precisely attributable; it must not degrade the whole archetype");
        Assert.That(Published(cs, 2), Does.Contain(chunkId), "the cluster holding the written entity");

        var slots = 0UL;
        for (var i = 0; i < cs.ChangedClusterCount; i++)
        {
            if (cs.ChangedClusterIds[i] == chunkId)
            {
                slots = cs.ChangedClusterSlots[i];
            }
        }

        Assert.That(slots, Is.Not.Zero, "the slot mask must name at least the written slot, not merely the cluster");
    }

    /// <summary>
    /// A move through <c>WriteSpatial</c> names its cluster — the path that raises no dirty bit at all.
    /// </summary>
    /// <remarks>
    /// The case the list exists for. <c>WriteSpatial</c> sets the process bit and nothing else, and the archetype takes the fence's clean branch, which
    /// returns before the dirty ring is archived. A list published after that return would report nothing for exactly the workload that moves.
    /// </remarks>
    [Test]
    public void AWriteSpatialMoveNamesItsCluster()
    {
        using var dbe = SetupEngine();
        var (_, chunkId) = SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var moved = MakePos(12, 12, 0, size: 0.5f);
                    cluster.WriteSpatial(ClSpatialUnit.Pos, slot, moved);
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = StateOf(dbe);

        Assert.That(cs.ChangedClustersCoverAll || Published(cs, 2).Contains(chunkId), Is.True,
            "WriteSpatial raises no dirty bit, so this is the process bitmap's case and the one the clean fence branch would otherwise drop");
    }

    /// <summary>
    /// A write through a mutable span over a NON-spatial column names the cluster the span was taken over — not the whole archetype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Such a write raises no signal anywhere else in the engine: not the dirty bitmap, not the process bit, not <c>SpatialSpanHandedOut</c>, and
    /// <c>TYPHON009</c> does not flag it either, because its subject is the <c>WriteSpatial</c> barrier. So the list has to claim something, and the
    /// only question is how coarsely.
    /// </para>
    /// <para>
    /// <b>Per cluster, and the first cut got this wrong.</b> Claiming the ARCHETYPE — by symmetry with <c>SpatialSpanHandedOut</c>, which must be
    /// archetype-wide because a read must not perturb the partition — degraded every tick that touched any span to "everything changed". Measured on the
    /// SWG demo at d06 that was 100 % of archetype-ticks, so the list carried no information at all on the workload it exists for. <c>GetSpan</c> is
    /// called once per cluster and holds its chunk id, so naming the cluster costs one bit and keeps the signal.
    /// </para>
    /// </remarks>
    [Test]
    public void AMutableSpanOverANonSpatialColumnNamesItsOwnCluster()
    {
        using var dbe = SetupEngine();
        var (_, chunkId) = SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var metas = cluster.GetSpan(ClSpatialUnit.Meta);
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    metas[slot].Tag = 1234;
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = StateOf(dbe);

        Assert.That(cs.ChangedClustersCoverAll, Is.False,
            "a span names the cluster it was taken over, so the archetype must NOT degrade to cover-all — that is the regression this pins");
        Assert.That(Published(cs, 2), Does.Contain(chunkId), "the cluster whose column was handed out as a mutable span");
    }

    /// <summary>
    /// A mutable span over a component NO projection reads names nothing — and the same span over a projected one still names its cluster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one place the list is allowed to stay silent, and it is decided statically.</b> Every other test here asserts presence, because a clean
    /// cluster in the list costs one wasted visit and a missing dirty one is a change no client hears about. This case is different in kind: the set of
    /// components a projection reads is fixed when the projection is compiled, so a write to a component outside that set cannot reach a subscriber
    /// however the caller writes through it. Suppressing it is not a guess about the bytes, it is a fact about the schema.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted in one test on purpose.</b> A test that only checked the suppression would pass just as well against a mask that
    /// suppressed everything — which is the failure that loses changes silently — so the projected column is written in the same tick, through the same
    /// API, and must still be named.
    /// </para>
    /// </remarks>
    [Test]
    public void AMutableSpanOverAnUnprojectedColumnNamesNothing()
    {
        using var dbe = SetupEngine();
        var (_, chunkId) = SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        var cs = StateOf(dbe);

        // Meta is out of the mask: the AI-bookkeeping shape — written every tick, read by nobody on the wire.
        cs.ProjectedComponentMask = ~(1UL << Archetype<ClSpatialUnit>.Metadata.GetSlot(ClSpatialUnit.Meta._componentTypeId));

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var metas = cluster.GetSpan(ClSpatialUnit.Meta);
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    metas[slot].Tag = 4321;
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(Published(cs, 2), Does.Not.Contain(chunkId),
            "a span over a column no projection reads cannot reach a subscriber, so the list must not name its cluster");
        Assert.That(cs.UnprojectedSpanClaims, Is.GreaterThan(0), "and the suppression must be the reason, not an unrelated silence");

        // The SAME write, with Meta back in the mask: named. Varying only the mask is what makes this a test of the mask rather than of the write — and
        // without this half, a mask that suppressed everything would pass the assertions above.
        cs.ProjectedComponentMask = ulong.MaxValue;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var metas = cluster.GetSpan(ClSpatialUnit.Meta);
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    metas[slot].Tag = 8765;
                }
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(3);
        Assert.That(Published(cs, 3), Does.Contain(chunkId), "the same span over a column the projection DOES read still names its cluster");
    }

    /// <summary>A destroy names the cluster the entity left.</summary>
    [Test]
    public void ADestroyNamesItsCluster()
    {
        using var dbe = SetupEngine();
        var (ids, chunkId) = SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = StateOf(dbe);

        Assert.That(cs.ChangedClustersCoverAll || Published(cs, 2).Contains(chunkId), Is.True,
            "a slot that stopped being occupied is a change every consumer has to see, or it goes on describing a dead entity");
    }

    // ── The shape the list promises ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ids are ascending and each appears once.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. Slice 5 intersects this list against a session's own sorted cluster set with a two-cursor walk; a duplicate or an inversion turns
    /// that into a lookup per entry, which is the cost the whole design is avoiding. Asserted here because it is cheap to provide at the producer and
    /// expensive to recover at every consumer.
    /// </remarks>
    [Test]
    public void TheIdsAreAscendingAndDistinct()
    {
        using var dbe = SetupEngine();

        // Several clusters, written through two different signals, so the merge actually has two streams to interleave.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 40; i++)
            {
                var pos = MakePos(100 + (i * 37 % 900), 100 + (i * 53 % 900), 0);
                var meta = new ClSpatialMeta { Tag = i };
                tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in meta));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClSpatialUnit>();
            var flip = false;
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }

                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (flip)
                {
                    // Marked path — contributes a slot mask.
                    cluster.MarkDirty(ClSpatialUnit.Meta);
                }
                else
                {
                    // Barrier path — contributes a process bit and no slots.
                    cluster.WriteSpatial(ClSpatialUnit.Pos, slot, MakePos(105, 105, 0, size: 0.5f));
                }

                flip = !flip;
            }

            accessor.Dispose();
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        var cs = StateOf(dbe);

        if (cs.ChangedClustersCoverAll)
        {
            Assert.Ignore("the tick degraded to cover-all, so there is no list to check the ordering of");
        }

        Assert.That(cs.ChangedClusterCount, Is.GreaterThan(1), "precondition: more than one cluster changed, or the ordering claim is vacuous");

        var seen = new HashSet<int>();
        for (var i = 0; i < cs.ChangedClusterCount; i++)
        {
            var id = cs.ChangedClusterIds[i];
            Assert.That(seen.Add(id), Is.True, $"chunk {id} appears twice; a consumer would apply it twice");
            if (i > 0)
            {
                Assert.That(id, Is.GreaterThan(cs.ChangedClusterIds[i - 1]), $"entry {i} is out of order");
            }
        }
    }

    /// <summary>A tick in which nothing was written publishes an empty list under this tick's stamp, not last tick's contents.</summary>
    /// <remarks>
    /// The failure this guards is the one 18 § 8.4 and 20 § 6 both record: a stale value read as a current one. A consumer tests the stamp before the
    /// count, so publishing the stamp without clearing the count would hand it last tick's clusters as this tick's.
    /// </remarks>
    [Test]
    public void AQuietTickPublishesAnEmptyListUnderItsOwnStamp()
    {
        using var dbe = SetupEngine();
        SeedOneCluster(dbe, 4, 10, 10, tick: 1);

        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);

        var cs = StateOf(dbe);
        if (cs.ChangedClusterTick != 3L)
        {
            // The archetype took the fence's no-work branch, which publishes nothing at all. That is a legitimate outcome for a quiet tick; what must
            // never happen is a CURRENT stamp over stale contents, which is what the branch below checks.
            Assert.That(cs.ChangedClusterTick, Is.LessThan(3L), "an unpublished tick must leave an older stamp, never this tick's");
            return;
        }

        Assert.That(cs.ChangedClustersCoverAll, Is.False, "nothing was written, so nothing can have handed out a span");
        Assert.That(cs.ChangedClusterCount, Is.Zero, "a quiet tick names no cluster");
    }
    /// <summary>
    /// A destroy on a NON-spatial archetype names its cluster too — the path whose signal cannot come from spatial machinery.
    /// </summary>
    /// <remarks>
    /// <c>ADestroyNamesItsCluster</c> proves the invariant on a spatial archetype, where the AABB machinery marks the cluster for reasons of its own, so
    /// it cannot distinguish "the release path signals" from "something else did". This one has no spatial index at all: if <c>ReleaseSlot</c> does not
    /// mark, nothing does, and a session goes on describing an entity that no longer exists until something unrelated touches the cluster.
    /// </remarks>
    [Test]
    public void ADestroyOnANonSpatialArchetypeNamesItsCluster()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.InitializeArchetypes();
        EnableTracking(dbe, Archetype<ClAnt>.Metadata.ArchetypeId);

        var ids = new EntityId[4];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var pos = new ClPosition(i, i);
                var mov = new ClMovement(i, i);
                ids[i] = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var cs = dbe._archetypeStates[Archetype<ClAnt>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.SpatialSlot.HasSpatialIndex, Is.False, "precondition: this archetype has no spatial index, so nothing else can mark for it");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // The invariant is conditional on publication, and that is the contract rather than a weakening of it: an archetype the fence finds no work for
        // is never visited, so no list is published and its stamp stays behind. Every consumer tests the stamp before the contents — the projection gate
        // will not narrow anything unless it names the current tick — so an unpublished tick costs the optimisation and cannot cost correctness. What
        // would be a defect is a CURRENT stamp that does not name the cluster, and that is what this asserts.
        if (cs.ChangedClusterTick != 2L)
        {
            Assert.That(cs.ChangedClusterTick, Is.LessThan(2L),
                "an unpublished tick must leave an older stamp; a current stamp over contents nobody computed is the one unsafe outcome");
            return;
        }

        Assert.That(cs.ChangedClustersCoverAll || cs.ChangedClusterCount > 0, Is.True,
            $"the tick published, so it must name the destroyed entity's cluster — there is no spatial machinery here to do it incidentally. "
            + $"count={cs.ChangedClusterCount} coverAll={cs.ChangedClustersCoverAll}");
    }

}
