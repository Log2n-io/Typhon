using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.F64.Pos3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct F64Pos3
{
    [Field]
    [SpatialIndex]
    public AABB3D Bounds;
}

[Archetype]
partial class F64Unit3 : Archetype<F64Unit3>
{
    public static readonly Comp<F64Pos3> Pos = Register<F64Pos3>();
}

[Component("Typhon.Test.F64.Pos2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct F64Pos2
{
    [Field]
    [SpatialIndex]
    public AABB2D Bounds;
}

[Archetype]
partial class F64Unit2 : Archetype<F64Unit2>
{
    public static readonly Comp<F64Pos2> Pos = Register<F64Pos2>();
}

[Component("Typhon.Test.F64.Ball", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct F64Ball
{
    [Field]
    [SpatialIndex]
    public BSphere3D Ball;
}

[Archetype]
partial class F64BallUnit : Archetype<F64BallUnit>
{
    public static readonly Comp<F64Ball> Ball = Register<F64Ball>();
}

/// <summary>
/// The f64 cluster tiers end to end (#914 phase C): an archetype declaring <c>AABB2D</c> / <c>AABB3D</c> / <c>BSphere3D</c> can be configured, written
/// through the spatial barrier, and — the part the tiers exist for — <b>queried at f64 precision</b>.
/// </summary>
/// <remarks>
/// <para><b>Why "it does not throw" is not the test.</b> Phases A and B made the world frame f64 and opened the write barrier; the gap this phase closes is
/// the one the issue's AC-7 names — the f64 tiers "currently also throw on query". A tier that could be declared and written but only read back through an
/// f32 query box would be exactly as useless as one that threw, and would fail more quietly. So the load-bearing cases here are the ones that put a
/// coordinate beyond f32's reach on BOTH sides of a comparison and assert the query still separates it, each with a precondition proving f32 could not.</para>
/// <para><b>The magnitude is chosen, not decorative.</b> At <c>2^36</c> one f32 step is 8 192 units — wider than the 1 000-unit cell — so any f32 anywhere in
/// the query path collapses the whole test world onto a handful of representable values. That is what makes the ablation sharp. At <c>2^30</c>, the first
/// magnitude tried, an f32 step is only 128 units and several of these tests would have passed against an un-widened build.</para>
/// <para>Storage is untouched by all of this: <c>C15</c> keeps every stored cluster bound f32 and CELL-RELATIVE, and these tests are written so that a change
/// widening it would not be needed to make them pass. The f64 lives in the world frame, the component field, the query box and the result — never in the
/// broadphase's SoA arrays.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class F64SpatialTierTests : TestBase<F64SpatialTierTests>
{
    /// <summary>2^36 — far enough out that one f32 step (8 192) is wider than a cell. See the fixture remarks.</summary>
    private const double FarOrigin = 68_719_476_736d;

    private const double CellSize = 1_000d;

    private const double WorldSpan = 64 * CellSize;

    private static SpatialGridConfig FarWorld => new(
        new Vector3D(FarOrigin, FarOrigin, FarOrigin),
        new Vector3D(FarOrigin + WorldSpan, FarOrigin + WorldSpan, FarOrigin + WorldSpan),
        CellSize);

    private DatabaseEngine Setup3D()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<F64Pos3>();
        dbe.ConfigureSpatialGrid(FarWorld);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static F64Pos3 PointAt(double x, double y, double z) =>
        new() { Bounds = new AABB3D { MinX = x, MinY = y, MinZ = z, MaxX = x, MaxY = y, MaxZ = z } };

    private static EntityId Spawn3D(DatabaseEngine dbe, double x, double y, double z)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<F64Unit3>(F64Unit3.Pos.Set(PointAt(x, y, z)));
        tx.Commit();
        return id;
    }

    private static ArchetypeClusterState StateOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>, new() =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;

    private static unsafe (int ChunkId, int Slot) LocateSlot<TArch>(DatabaseEngine dbe, EntityId id) where TArch : Archetype<TArch>, new()
    {
        var cs = StateOf<TArch>(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (cid, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (-1, -1);
    }

    private static void WriteSpatialTo(DatabaseEngine dbe, EntityId id, double x, double y, double z)
    {
        var (chunkId, slot) = LocateSlot<F64Unit3>(dbe, id);
        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "entity must be resident in a cluster before a barrier write");

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<F64Unit3>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId == chunkId)
                {
                    cluster.WriteSpatial(F64Unit3.Pos, slot, PointAt(x, y, z));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    /// <summary>Every entity the AABB query returns for <paramref name="box"/>, as ids.</summary>
    private static List<long> QueryIds(DatabaseEngine dbe, in AABB3D box)
    {
        var hits = new List<long>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var q = dbe.ClusterSpatialQuery<F64Unit3>().AABB(in box);
        try
        {
            while (q.MoveNext())
            {
                hits.Add(q.Current.EntityId);
            }
        }
        finally
        {
            q.Dispose();
        }
        return hits;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The gate — an f64 archetype exists at all
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An <c>AABB3D</c> archetype configures, spawns and queries — the three things that each used to throw.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void Aabb3DArchetype_Configures_Spawns_AndQueries()
    {
        using var dbe = Setup3D();

        var id = Spawn3D(dbe, FarOrigin + 105d, FarOrigin + 105d, FarOrigin + 105d);
        dbe.WriteTickFence(1);

        var hits = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin, MinY = FarOrigin, MinZ = FarOrigin,
            MaxX = FarOrigin + 200d, MaxY = FarOrigin + 200d, MaxZ = FarOrigin + 200d,
        });

        Assert.That(hits, Is.EquivalentTo(new[] { (long)id.RawValue }));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The payoff — a query box f32 cannot express
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// At <c>2^36</c> a 10-unit query box selects one of two entities 795 units apart. In f32 the box's two corners are the same number, so the same query
    /// would return both.
    /// </summary>
    /// <remarks>
    /// This is the case that justifies widening the query box rather than narrowing it at the door. Note where the f32 build would go wrong: the broadphase
    /// is unaffected — cluster bounds are cell-relative, so 105 and 900 are small and perfectly distinct — and the entity's own coordinate is exact because
    /// the component is <c>AABB3D</c>. It is the NARROWPHASE comparison, world coordinate against world query bound, that collapses. So the failure mode is
    /// not "returns nothing", which a smoke test would catch; it is "returns the whole cell", which only a differential assertion catches.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-06")]
    public void NarrowQueryBoxAtExtent_SelectsOneOfTwoEntitiesInTheSameCell()
    {
        using var dbe = Setup3D();

        var near = Spawn3D(dbe, FarOrigin + 105d, FarOrigin + 105d, FarOrigin + 105d);
        Spawn3D(dbe, FarOrigin + 900d, FarOrigin + 105d, FarOrigin + 105d);
        dbe.WriteTickFence(1);

        // PRECONDITION: in f32 the query box below is degenerate — both corners round to the same value — so an f32 narrowphase cannot separate the two.
        Assert.That((float)(FarOrigin + 100d), Is.EqualTo((float)(FarOrigin + 110d)),
            "PRECONDITION: at this magnitude one f32 step must exceed the query box's width, or the ablation proves nothing.");
        Assert.That((float)(FarOrigin + 105d), Is.EqualTo((float)(FarOrigin + 900d)),
            "PRECONDITION: the two entity coordinates must also be indistinguishable in f32.");

        var hits = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin + 100d, MinY = FarOrigin + 100d, MinZ = FarOrigin + 100d,
            MaxX = FarOrigin + 110d, MaxY = FarOrigin + 110d, MaxZ = FarOrigin + 110d,
        });

        Assert.That(hits, Is.EquivalentTo(new[] { (long)near.RawValue }),
            "the f64 query box must select only the entity inside it — an f32 box would return both, since its two corners are the same float");
    }

    /// <summary>The bounds handed back on a hit are the f64 the component stores, not a narrowed copy.</summary>
    /// <remarks>
    /// Separate from the case above because it fails differently: the query could select correctly and still report a coordinate the caller cannot use. The
    /// assertion is exact equality — at this magnitude an f32 round trip is off by up to 4 096 units, so <c>Within</c> would hide the defect it is here to
    /// find.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-06")]
    public void ResultBoundsComeBackAtFullPrecision()
    {
        using var dbe = Setup3D();

        const double X = FarOrigin + 105.5d;
        const double Y = FarOrigin + 206.25d;
        const double Z = FarOrigin + 307.125d;
        Spawn3D(dbe, X, Y, Z);
        dbe.WriteTickFence(1);

        Assert.That((float)X, Is.Not.EqualTo(X).Within(1d),
            "PRECONDITION: the coordinate must be one f32 cannot hold, or exact equality below would pass either way.");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var q = dbe.ClusterSpatialQuery<F64Unit3>().AABB(in ThreeHundredBox);
        try
        {
            Assert.That(q.MoveNext(), Is.True, "the entity must be found before its bounds can be checked");
            var hit = q.Current;   // copied out: a ref struct enumerator cannot be captured by Assert.Multiple's lambda
            Assert.Multiple(() =>
            {
                Assert.That(hit.MinX, Is.EqualTo(X), "MinX must be the stored double, exactly");
                Assert.That(hit.MinY, Is.EqualTo(Y));
                Assert.That(hit.MinZ, Is.EqualTo(Z));
                Assert.That(hit.MaxX, Is.EqualTo(X));
            });
        }
        finally
        {
            q.Dispose();
        }
    }

    /// <summary>Every entity in the world, read through the query path, as (id, bounds) — the population an oracle measures against.</summary>
    private static unsafe List<(long Id, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)> ReadAllEntities(DatabaseEngine dbe)
    {
        var all = new List<(long, double, double, double, double, double, double)>();
        var cs = StateOf<F64Unit3>(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var ss = cs.SpatialSlot;
            var compOffset = cs.Layout.ComponentOffset(ss.Slot);
            var compStride = cs.Layout.ComponentSize(ss.Slot);
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    ref var b = ref *(AABB3D*)(clusterBase + compOffset + slot * compStride + ss.FieldOffset);
                    var id = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8);
                    all.Add((id, b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return all;
    }

    private static readonly AABB3D ThreeHundredBox = new()
    {
        MinX = FarOrigin, MinY = FarOrigin, MinZ = FarOrigin,
        MaxX = FarOrigin + 400d, MaxY = FarOrigin + 400d, MaxZ = FarOrigin + 400d,
    };

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The dimensionality trap the f64 tiers walked into
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A 3D <b>f64</b> archetype is treated as 3D — not collapsed onto the grid's flat plane the way the old two-way <c>AABB3F || BSphere3F</c> test would
    /// have done.
    /// </summary>
    /// <remarks>
    /// The nine sites that spelled dimensionality that way were correct for as long as <c>ValidateSupportedFieldType</c> rejected f64. Opening that gate
    /// turned every one of them into a silent misclassification: the enumerator would have pinned <c>_cellMinZ = _cellMaxZ = FlatPlaneZ</c> and swept one
    /// Z plane of a 64-deep grid, so an entity anywhere else on Z would simply never be visited. Two entities differing ONLY on Z is the shape that catches
    /// it — a pair that also differed on X would be found through the X range whatever the Z handling did.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-06")]
    public void ZSeparatedEntities_AreBothFoundAndSeparatelySelectable()
    {
        using var dbe = Setup3D();

        var low = Spawn3D(dbe, FarOrigin + 500d, FarOrigin + 500d, FarOrigin + 500d);
        var high = Spawn3D(dbe, FarOrigin + 500d, FarOrigin + 500d, FarOrigin + 20_500d);
        dbe.WriteTickFence(1);

        var spanningZ = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin, MinY = FarOrigin, MinZ = FarOrigin,
            MaxX = FarOrigin + 1_000d, MaxY = FarOrigin + 1_000d, MaxZ = FarOrigin + WorldSpan,
        });

        var lowSliceOnly = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin, MinY = FarOrigin, MinZ = FarOrigin,
            MaxX = FarOrigin + 1_000d, MaxY = FarOrigin + 1_000d, MaxZ = FarOrigin + 1_000d,
        });

        Assert.Multiple(() =>
        {
            Assert.That(spanningZ, Is.EquivalentTo(new[] { (long)low.RawValue, (long)high.RawValue }),
                "a box spanning the world's depth must reach BOTH entities — a flat-plane collapse would drop the high one");
            Assert.That(lowSliceOnly, Is.EquivalentTo(new[] { (long)low.RawValue }),
                "and a box covering only the first Z plane must exclude the high one — otherwise Z is not being tested at all");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Writing through the barrier
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An <c>AABB3D</c> entity moves through <c>WriteSpatial</c>, and the query follows it.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void Aabb3D_BarrierWrite_MovesTheEntity()
    {
        using var dbe = Setup3D();
        dbe.SetSpatialBarrierOnly<F64Unit3>();

        var id = Spawn3D(dbe, FarOrigin + 105d, FarOrigin + 105d, FarOrigin + 105d);
        dbe.WriteTickFence(1);

        Assert.DoesNotThrow(() => WriteSpatialTo(dbe, id, FarOrigin + 4_105d, FarOrigin + 105d, FarOrigin + 105d),
            "AABB3D is the widest tier the grid stores — the barrier must accept it");
        dbe.WriteTickFence(2);

        var atOrigin = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin, MinY = FarOrigin, MinZ = FarOrigin,
            MaxX = FarOrigin + 1_000d, MaxY = FarOrigin + 1_000d, MaxZ = FarOrigin + 1_000d,
        });
        var atDestination = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin + 4_000d, MinY = FarOrigin, MinZ = FarOrigin,
            MaxX = FarOrigin + 5_000d, MaxY = FarOrigin + 1_000d, MaxZ = FarOrigin + 1_000d,
        });

        Assert.Multiple(() =>
        {
            Assert.That(atOrigin, Is.Empty, "the entity must have LEFT its old box — a write that only stored bytes would leave it there");
            Assert.That(atDestination, Is.EquivalentTo(new[] { (long)id.RawValue }));
        });
    }

    /// <summary>The 2D f64 tier writes and queries too — the same omission, one dimension down.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void Aabb2D_SpawnsAndQueries()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<F64Pos2>();
        dbe.ConfigureSpatialGrid(FarWorld);
        dbe.InitializeArchetypes();
        using var _ = dbe;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<F64Unit2>(F64Unit2.Pos.Set(new F64Pos2
            {
                Bounds = new AABB2D { MinX = FarOrigin + 105d, MinY = FarOrigin + 105d, MaxX = FarOrigin + 105d, MaxY = FarOrigin + 105d },
            }));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var hits = new List<long>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var box = new AABB2D { MinX = FarOrigin + 100d, MinY = FarOrigin + 100d, MaxX = FarOrigin + 110d, MaxY = FarOrigin + 110d };
            var q = dbe.ClusterSpatialQuery<F64Unit2>().AABB(in box);
            try
            {
                while (q.MoveNext())
                {
                    hits.Add(q.Current.EntityId);
                }
            }
            finally
            {
                q.Dispose();
            }
        }

        Assert.That(hits, Is.EquivalentTo(new[] { (long)id.RawValue }));
    }

    /// <summary>The f64 sphere tier answers a <c>Radius</c> query, with an f64 <c>DistanceSq</c>.</summary>
    /// <remarks>
    /// <c>DistanceSq</c> is the field precision bites hardest on: it is a SQUARE, so at <c>2^36</c> the closest-point difference would be computed between
    /// two values that are equal in f32 and come out 0 for every candidate — which reads as "everything is at the query centre" rather than as an error.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void BSphere3D_RadiusQuery_RanksByAnF64Distance()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<F64Ball>();
        dbe.ConfigureSpatialGrid(FarWorld);
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<F64BallUnit>(F64BallUnit.Ball.Set(new F64Ball
            {
                Ball = new BSphere3D { CenterX = FarOrigin + 110d, CenterY = FarOrigin + 100d, CenterZ = FarOrigin + 100d, Radius = 1d },
            }));
            tx.Spawn<F64BallUnit>(F64BallUnit.Ball.Set(new F64Ball
            {
                Ball = new BSphere3D { CenterX = FarOrigin + 150d, CenterY = FarOrigin + 100d, CenterZ = FarOrigin + 100d, Radius = 1d },
            }));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var distances = new List<double>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var sphere = new BSphere3D { CenterX = FarOrigin + 100d, CenterY = FarOrigin + 100d, CenterZ = FarOrigin + 100d, Radius = 100d };
            var q = dbe.ClusterSpatialQuery<F64BallUnit>().Radius(in sphere);
            try
            {
                while (q.MoveNext())
                {
                    distances.Add(q.Current.DistanceSq);
                }
            }
            finally
            {
                q.Dispose();
            }
        }

        distances.Sort();
        Assert.Multiple(() =>
        {
            Assert.That(distances, Has.Count.EqualTo(2), "both spheres are inside the query radius");
            Assert.That(distances[0], Is.GreaterThan(0d).And.LessThan(distances[1]),
                "the two must be at DIFFERENT non-zero distances — in f32 every difference at this magnitude rounds to 0 and both would tie at 0");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The tier check still bites
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Querying an f64 archetype with an f32 box still throws — opening the tiers did not open the type check.</summary>
    /// <remarks>
    /// Worth pinning precisely because the enumerator now takes doubles: an <c>AABB3F</c>'s floats would widen silently and the query would "work", quietly
    /// answering a question the caller did not ask at a precision they did not choose. The tier check is what keeps that a compile-time-shaped error rather
    /// than a rounding one.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-06")]
    public void QueryingAnF64ArchetypeWithAnF32Box_StillThrows()
    {
        using var dbe = Setup3D();
        Spawn3D(dbe, FarOrigin + 105d, FarOrigin + 105d, FarOrigin + 105d);
        dbe.WriteTickFence(1);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var f32Box = new AABB3F { MinX = 0f, MinY = 0f, MinZ = 0f, MaxX = 1f, MaxY = 1f, MaxZ = 1f };
        Assert.Throws<InvalidOperationException>(() => dbe.ClusterSpatialQuery<F64Unit3>().AABB(in f32Box).Dispose());
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The world box read back off a cluster
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>ClusterRef.SpatialBounds</c> reports a world box that actually contains its entity, at a magnitude where an f32 box could not.</summary>
    /// <remarks>
    /// The <c>C15</c> stored bound is f32 and cell-relative and stays that way — what changed is the conversion OUT of that frame, which used to narrow to
    /// f32 world space and so reported every cluster in this world at one of a few positions 8 192 units apart. Containment is the assertion because it is
    /// the property callers rely on (<c>CA-01</c>), and it is exactly what a quantised conversion breaks.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void SpatialBounds_ReportsAWorldBoxContainingItsEntity_AtExtent()
    {
        using var dbe = Setup3D();

        const double X = FarOrigin + 4_321d;
        Spawn3D(dbe, X, FarOrigin + 105d, FarOrigin + 105d);
        dbe.WriteTickFence(1);

        var found = false;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<F64Unit3>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var box = cluster.SpatialBounds;
                    if (box.IsEmpty)
                    {
                        continue;
                    }
                    found = true;
                    Assert.That(box.MinX, Is.LessThanOrEqualTo(X), "the world box must contain the entity on the low side");
                    Assert.That(box.MaxX, Is.GreaterThanOrEqualTo(X), "and on the high side");

                    // The ablation, stated rather than performed: the same conversion narrowed to f32.
                    Assert.That((float)box.MinX, Is.EqualTo((float)box.MaxX),
                        "PRECONDITION: an f32 world box cannot distinguish this cluster's corners, which is why the conversion returns doubles.");
                }
            }
            finally
            {
                accessor.Dispose();
            }
            tx.Commit();
        }

        Assert.That(found, Is.True, "a cluster with a real bound must have been visited");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-2 / AC-4 — precision and completeness, measured rather than asserted
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-2</c>: a world of extent ≥ 10⁹ with 10³ cells resolves entities to the right cell and returns them from a query box <b>1 unit</b> wide.
    /// </summary>
    /// <remarks>
    /// The issue names the box width because it is what separates this from "a big number went in and came out". A 1-unit box at 2³⁶ is four thousand
    /// times narrower than one f32 step, so an f32 anywhere on the path cannot express it at all — it degenerates to a point that is not even at the right
    /// place. The precondition below states that in the test rather than in a comment.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("SQ-06")]
    public void OneUnitQueryBoxAtExtent_ReturnsExactlyTheEntityInsideIt()
    {
        using var dbe = Setup3D();

        var inside = Spawn3D(dbe, FarOrigin + 500.25d, FarOrigin + 500.25d, FarOrigin + 500.25d);
        Spawn3D(dbe, FarOrigin + 502.75d, FarOrigin + 500.25d, FarOrigin + 500.25d);   // 2.5 units away — outside a 1-unit box
        dbe.WriteTickFence(1);

        Assert.That((float)(FarOrigin + 500d), Is.EqualTo((float)(FarOrigin + 501d)),
            "PRECONDITION: one f32 step must be wider than the 1-unit query box, or this measures nothing.");

        var hits = QueryIds(dbe, new AABB3D
        {
            MinX = FarOrigin + 500d, MinY = FarOrigin + 500d, MinZ = FarOrigin + 500d,
            MaxX = FarOrigin + 501d, MaxY = FarOrigin + 501d, MaxZ = FarOrigin + 501d,
        });

        Assert.That(hits, Is.EquivalentTo(new[] { (long)inside.RawValue }));
    }

    /// <summary>
    /// <c>AC-4</c>: <c>SQ-01</c> against brute force, in f64, over a population of boxes — including query boxes whose faces land exactly on cell
    /// boundaries at extent, which is where an f32 conversion drops a cluster grazing the edge.
    /// </summary>
    /// <remarks>
    /// <para><b>The oracle reads the entity bounds directly from cluster storage</b> rather than through another query, so the index is not being compared
    /// against itself. It is the same shape <c>ClusterKnnTests</c> uses, minus that fixture's caveat: this one does not route through <c>QueryAabb</c>, so an
    /// entity lost from the index shows up as a MISSING hit rather than vanishing from both sides.</para>
    /// <para>The boundary cases are the point. A cell face at extent is where the query box's cell-relative conversion has to round outward: land the face
    /// exactly on it and any inward rounding drops the entity sitting on the boundary, which is <c>SQ-01</c>'s silent direction and invisible to a random
    /// sample.</para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("SQ-01")]
    public void AabbQueryMatchesBruteForce_AtExtent_IncludingBoxesOnCellBoundaries()
    {
        using var dbe = Setup3D();

        // Deterministic spread over several cells on every axis, with a deliberate cluster of entities ON cell boundaries.
        var rng = new Random(20260908);
        for (var i = 0; i < 400; i++)
        {
            Spawn3D(dbe,
                FarOrigin + (rng.NextDouble() * 4d * CellSize),
                FarOrigin + (rng.NextDouble() * 4d * CellSize),
                FarOrigin + (rng.NextDouble() * 4d * CellSize));
        }
        for (var c = 1; c <= 3; c++)
        {
            Spawn3D(dbe, FarOrigin + (c * CellSize), FarOrigin + (c * CellSize), FarOrigin + (c * CellSize));
        }
        dbe.WriteTickFence(1);

        var population = ReadAllEntities(dbe);
        Assert.That(population, Has.Count.EqualTo(403), "the index must hold every spawned entity before anything is compared against it");

        var boxes = new List<AABB3D>();
        for (var c = 0; c <= 4; c++)
        {
            // Faces exactly on a cell boundary, on the low side and on the high side.
            boxes.Add(new AABB3D
            {
                MinX = FarOrigin + (c * CellSize), MinY = FarOrigin, MinZ = FarOrigin,
                MaxX = FarOrigin + ((c + 1) * CellSize), MaxY = FarOrigin + (4d * CellSize), MaxZ = FarOrigin + (4d * CellSize),
            });
        }
        for (var i = 0; i < 30; i++)
        {
            var x = rng.NextDouble() * 4d * CellSize;
            var y = rng.NextDouble() * 4d * CellSize;
            var z = rng.NextDouble() * 4d * CellSize;
            var w = 0.5d + (rng.NextDouble() * 600d);
            boxes.Add(new AABB3D
            {
                MinX = FarOrigin + x, MinY = FarOrigin + y, MinZ = FarOrigin + z,
                MaxX = FarOrigin + x + w, MaxY = FarOrigin + y + w, MaxZ = FarOrigin + z + w,
            });
        }

        var checkedBoxes = 0;
        foreach (var box in boxes)
        {
            var expected = new HashSet<long>();
            foreach (var e in population)
            {
                if (e.MaxX >= box.MinX && e.MinX <= box.MaxX
                    && e.MaxY >= box.MinY && e.MinY <= box.MaxY
                    && e.MaxZ >= box.MinZ && e.MinZ <= box.MaxZ)
                {
                    expected.Add(e.Id);
                }
            }

            var actual = new HashSet<long>(QueryIds(dbe, in box));

            // SQ-01 is about FALSE NEGATIVES specifically, so the missing set is named separately from the extra set — a broadphase that is merely generous
            // is allowed by the rule, one that drops an entity is not.
            var missing = new HashSet<long>(expected);
            missing.ExceptWith(actual);
            Assert.That(missing, Is.Empty, $"SQ-01: the query dropped {missing.Count} entities the brute-force scan found, for box starting at {box.MinX}");
            Assert.That(actual, Is.SubsetOf(expected), "the query returned an entity the brute-force scan says is outside the box");
            checkedBoxes++;
        }

        Assert.That(checkedBoxes, Is.EqualTo(35));
    }

    /// <summary><c>AC-5</c>: <c>CA-01</c> holds for f64 entities — every cluster's f32 cell-relative bound contains every f64 entity it holds.</summary>
    /// <remarks>
    /// This is the invariant that would break if <c>C15</c> had been misread and the stored bound widened, or if the directed rounding in
    /// <c>ToCellRelativeMin/Max</c> had been dropped when the f64 tiers arrived. The rebasing here goes through the SAME conversion production uses, which
    /// is what makes the assertion meaningful: a bound rebased by a bare subtraction would agree with a bound stored by a bare subtraction and prove nothing.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-01")]
    public unsafe void ClusterBoundsContainTheirF64Entities_AfterAFence()
    {
        using var dbe = Setup3D();
        dbe.SetSpatialBarrierOnly<F64Unit3>();

        var ids = new List<EntityId>();
        var rng = new Random(4242);
        for (var i = 0; i < 120; i++)
        {
            ids.Add(Spawn3D(dbe,
                FarOrigin + (rng.NextDouble() * 3d * CellSize),
                FarOrigin + (rng.NextDouble() * 3d * CellSize),
                FarOrigin + (rng.NextDouble() * 3d * CellSize)));
        }
        dbe.WriteTickFence(1);
        AssertCa01(dbe, "after the spawn fence");

        // Move a third of them through the barrier, then fence again: the grow-CAS path and the shrink path both run.
        for (var i = 0; i < ids.Count; i += 3)
        {
            WriteSpatialTo(dbe, ids[i],
                FarOrigin + (rng.NextDouble() * 3d * CellSize),
                FarOrigin + (rng.NextDouble() * 3d * CellSize),
                FarOrigin + (rng.NextDouble() * 3d * CellSize));
        }
        dbe.WriteTickFence(2);
        AssertCa01(dbe, "after a tick of f64 barrier writes");
    }

    private static unsafe void AssertCa01(DatabaseEngine dbe, string what)
    {
        var cs = StateOf<F64Unit3>(dbe);
        var grid = dbe.SpatialGrid;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var ss = cs.SpatialSlot;
            var compOffset = cs.Layout.ComponentOffset(ss.Slot);
            var compStride = cs.Layout.ComponentSize(ss.Slot);
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var cellKey = cs.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                grid.CellOrigin(cellKey, out double ox, out double oy, out double oz);
                ref readonly var box = ref cs.ClusterAabbs[chunkId];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    ref var b = ref *(AABB3D*)(clusterBase + compOffset + slot * compStride + ss.FieldOffset);

                    Assert.That(box.MinX, Is.LessThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMin(b.MinX, ox)), $"CA-01 X-min, cluster {chunkId} {what}");
                    Assert.That(box.MinY, Is.LessThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMin(b.MinY, oy)), $"CA-01 Y-min, cluster {chunkId} {what}");
                    Assert.That(box.MinZ, Is.LessThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMin(b.MinZ, oz)), $"CA-01 Z-min, cluster {chunkId} {what}");
                    Assert.That(box.MaxX, Is.GreaterThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMax(b.MaxX, ox)), $"CA-01 X-max, cluster {chunkId} {what}");
                    Assert.That(box.MaxY, Is.GreaterThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMax(b.MaxY, oy)), $"CA-01 Y-max, cluster {chunkId} {what}");
                    Assert.That(box.MaxZ, Is.GreaterThanOrEqualTo(ClusterSpatialAabb.ToCellRelativeMax(b.MaxZ, oz)), $"CA-01 Z-max, cluster {chunkId} {what}");
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-1's remaining shapes — ray, frustum, kNN
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A ray fired at extent hits the entity in its path and misses the one beside it.</summary>
    /// <remarks>
    /// The two entities are 2.5 units apart on Y — well under one f32 step here — so an f32 ray cannot distinguish "through the first" from "through the
    /// second". Both would be hit, or neither, depending on which way the origin rounded.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void RayAtExtent_HitsTheEntityInItsPathAndNotTheOneBesideIt()
    {
        using var dbe = Setup3D();

        var onAxis = Spawn3D(dbe, FarOrigin + 900d, FarOrigin + 500d, FarOrigin + 500d);
        Spawn3D(dbe, FarOrigin + 900d, FarOrigin + 502.5d, FarOrigin + 500d);
        dbe.WriteTickFence(1);

        Assert.That((float)(FarOrigin + 500d), Is.EqualTo((float)(FarOrigin + 502.5d)),
            "PRECONDITION: the two entities must be indistinguishable in f32, or the ray's precision is not what is under test.");

        var cs = StateOf<F64Unit3>(dbe);
        var buffer = new (long entityId, double distance)[16];
        int n;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            n = cs.QueryRay(dbe.SpatialGrid, FarOrigin, FarOrigin + 500d, FarOrigin + 500d, 1d, 0d, 0d, 4d * CellSize, buffer, categoryMask: 0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(1), "exactly the entity on the ray's own line");
            Assert.That(buffer[0].entityId, Is.EqualTo((long)onAxis.RawValue));
            Assert.That(buffer[0].distance, Is.EqualTo(900d).Within(1e-6d), "the distance must be a world distance, not a quantised one");
        });
    }

    /// <summary>A frustum at extent selects the half-space it names, from a <b>tight</b> bounding box several cells out.</summary>
    /// <remarks>
    /// <para>The planes were doubles before #919 — it is the caller's BOUNDING BOX that was narrowed, and the box is what resolves to a cell range. So the
    /// failure this pins is coarser than a dropped entity: at 2³⁶ both X corners of a 200-unit box round to the same float, the range collapses to cell 0,
    /// and every cell the view actually covers is skipped. A whole-world box would not show it — <c>WorldToCellRange</c> clamps, and a collapsed range still
    /// happens to contain the origin cell.</para>
    /// <para>Hence the entity under test sits in cell 3 rather than cell 0, and the box is drawn tightly around it.</para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void FrustumAtExtent_SelectsTheHalfSpaceItNames_FromATightBoxSeveralCellsOut()
    {
        using var dbe = Setup3D();

        var inside = Spawn3D(dbe, FarOrigin + 3_500d, FarOrigin + 500d, FarOrigin + 500d);
        Spawn3D(dbe, FarOrigin + 20_000d, FarOrigin + 500d, FarOrigin + 500d);   // beyond the plane
        dbe.WriteTickFence(1);

        Assert.That((float)(FarOrigin + 3_400d), Is.EqualTo((float)(FarOrigin + 3_600d)),
            "PRECONDITION: the box's two X corners must be the same float, or the bounding box's precision is not what is under test.");

        // One plane: x <= FarOrigin + 10000, written as the inward half-space (-1, 0, 0, d).
        var planes = new double[] { -1d, 0d, 0d, FarOrigin + 10_000d };
        var results = new long[16];
        int n;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            n = StateOf<F64Unit3>(dbe).QueryFrustum(dbe.SpatialGrid, planes, 1,
                new Vector3Like(FarOrigin + 3_400d, FarOrigin, FarOrigin),
                new Vector3Like(FarOrigin + 3_600d, FarOrigin + WorldSpan, FarOrigin + WorldSpan),
                results, categoryMask: 0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(1), "the entity inside both the box and the half-space — an f32 box collapses the cell range and finds nothing");
            Assert.That(results[0], Is.EqualTo((long)inside.RawValue));
        });
    }

    /// <summary>kNN at extent orders neighbours by a distance f32 could not have told apart.</summary>
    /// <remarks>
    /// The three entities are 1, 2 and 3 units from the query point. In f32 at this magnitude all three squared distances collapse to the same value, so the
    /// ordering would be whatever the heap happened to produce — and the early-termination bound, which is the whole point of the ring search, would compare
    /// equal to it and could prune a nearer cluster.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void KnnAtExtent_OrdersNeighboursByAnF64Distance()
    {
        using var dbe = Setup3D();

        var a = Spawn3D(dbe, FarOrigin + 501d, FarOrigin + 500d, FarOrigin + 500d);
        var b = Spawn3D(dbe, FarOrigin + 502d, FarOrigin + 500d, FarOrigin + 500d);
        var c = Spawn3D(dbe, FarOrigin + 503d, FarOrigin + 500d, FarOrigin + 500d);
        dbe.WriteTickFence(1);

        var buffer = new (long entityId, double distSq)[3];
        int n;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            n = StateOf<F64Unit3>(dbe).QueryNearest(dbe.SpatialGrid, FarOrigin + 500d, FarOrigin + 500d, FarOrigin + 500d, 3, buffer, categoryMask: 0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(3));
            Assert.That(buffer[0].entityId, Is.EqualTo((long)a.RawValue), "nearest first");
            Assert.That(buffer[1].entityId, Is.EqualTo((long)b.RawValue));
            Assert.That(buffer[2].entityId, Is.EqualTo((long)c.RawValue));
            Assert.That(buffer[0].distSq, Is.EqualTo(1d).Within(1e-6d));
            Assert.That(buffer[1].distSq, Is.EqualTo(4d).Within(1e-6d));
            Assert.That(buffer[2].distSq, Is.EqualTo(9d).Within(1e-6d));
        });
    }
}
