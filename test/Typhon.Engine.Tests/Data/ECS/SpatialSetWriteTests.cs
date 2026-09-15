using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.SetW.A2F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWA2F
{
    [Field]
    [SpatialIndex]
    public AABB2F Box;
}

[Archetype]
partial class SetWA2FUnit : Archetype<SetWA2FUnit>
{
    public static readonly Comp<SetWA2F> Box = Register<SetWA2F>();
}

[Component("Typhon.Test.SetW.A3F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWA3F
{
    [Field]
    [SpatialIndex]
    public AABB3F Box;
}

[Archetype]
partial class SetWA3FUnit : Archetype<SetWA3FUnit>
{
    public static readonly Comp<SetWA3F> Box = Register<SetWA3F>();
}

[Component("Typhon.Test.SetW.S2F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWS2F
{
    [Field]
    [SpatialIndex]
    public BSphere2F Ball;
}

[Archetype]
partial class SetWS2FUnit : Archetype<SetWS2FUnit>
{
    public static readonly Comp<SetWS2F> Ball = Register<SetWS2F>();
}

[Component("Typhon.Test.SetW.S3F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWS3F
{
    [Field]
    [SpatialIndex]
    public BSphere3F Ball;
}

[Archetype]
partial class SetWS3FUnit : Archetype<SetWS3FUnit>
{
    public static readonly Comp<SetWS3F> Ball = Register<SetWS3F>();
}

[Component("Typhon.Test.SetW.A2D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWA2D
{
    [Field]
    [SpatialIndex]
    public AABB2D Box;
}

[Archetype]
partial class SetWA2DUnit : Archetype<SetWA2DUnit>
{
    public static readonly Comp<SetWA2D> Box = Register<SetWA2D>();
}

[Component("Typhon.Test.SetW.A3D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWA3D
{
    [Field]
    [SpatialIndex]
    public AABB3D Box;
}

[Archetype]
partial class SetWA3DUnit : Archetype<SetWA3DUnit>
{
    public static readonly Comp<SetWA3D> Box = Register<SetWA3D>();
}

[Component("Typhon.Test.SetW.S2D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWS2D
{
    [Field]
    [SpatialIndex]
    public BSphere2D Ball;
}

[Archetype]
partial class SetWS2DUnit : Archetype<SetWS2DUnit>
{
    public static readonly Comp<SetWS2D> Ball = Register<SetWS2D>();
}

[Component("Typhon.Test.SetW.S3D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SetWS3D
{
    [Field]
    [SpatialIndex]
    public BSphere3D Ball;
}

[Archetype]
partial class SetWS3DUnit : Archetype<SetWS3DUnit>
{
    public static readonly Comp<SetWS3D> Ball = Register<SetWS3D>();
}

/// <summary>
/// The slot-set <c>ClusterRef.WriteSpatial</c> (rule CA-03): one call per cluster leaves the cluster exactly as the same single-slot writes, made in
/// ascending slot order, would.
/// </summary>
[TestFixture]
[NonParallelizable]
class SpatialSetWriteTests : TestBase<SpatialSetWriteTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const double SpawnLow = 50;
    private const double SpawnHigh = 450;
    private const int Population = 600;
    private const int Seed = 20260915;

    /// <summary>A block this dense, in a cell of its own, fills clusters to their last slot whatever the tier's cluster size.</summary>
    private const int DenseCount = 64;
    private const double DenseCentre = 650;

    /// <summary>Distinctive substring of CA-03's rejection messages.</summary>
    private const string Ca03Marker = "CA-03 violated";

    public enum Tier
    {
        Aabb2F,
        Aabb3F,
        BSphere2F,
        BSphere3F,
        Aabb2D,
        Aabb3D,
        BSphere2D,
        BSphere3D,
    }

    /// <summary>What the write tick does to each cluster.</summary>
    public enum Motion
    {
        /// <summary>Every entity moves, by a displacement drawn from its cluster and slot.</summary>
        MoveAll,

        /// <summary>Every other entity is written back unchanged: a partial mask, and a write that changes nothing.</summary>
        RewriteEveryOther,
    }

    /// <summary>
    /// Two engines built from one seed; every entity is moved by the same displacement in both, slot by slot in one and one call per cluster in the other.
    /// Before the fence the two must hold the same slot values, bounds, shrink and migration flags, destination hints, counters and process bits. After the
    /// fence every entity must sit in the same cell in both, and every bound must contain its entities (CA-01). The population is dense enough that most
    /// clusters hold many entities and some are full, and the moves reach the cases each check needs: crossings, several of them in one cluster, inward
    /// extremes and the hysteresis band.
    /// </summary>
    [TestCase(Tier.Aabb2F)]
    [TestCase(Tier.Aabb3F)]
    [TestCase(Tier.BSphere2F)]
    [TestCase(Tier.BSphere3F)]
    [TestCase(Tier.Aabb2D)]
    [TestCase(Tier.Aabb3D)]
    [TestCase(Tier.BSphere2D)]
    [TestCase(Tier.BSphere3D)]
    [VerifiesRule("CA-03")]
    public void ASetWrite_LeavesTheClusterAsItsSingleWrites(Tier tier)
    {
        var single = Run(tier, setWrite: false, Motion.MoveAll);
        var set = Run(tier, setWrite: true, Motion.MoveAll);

        Assert.That(set.Layout, Is.EqualTo(single.Layout), "precondition: the two engines built different clusters from one seed");
        Assert.That(single.Layout.Values.Count(bits => BitOperations.PopCount(bits) >= 4), Is.GreaterThan(single.Layout.Count / 2),
            "precondition: most clusters should hold several entities, or the call has little to batch");
        Assert.That(single.Layout.Values.Any(bits => bits == single.FullMask), Is.True, "precondition: no cluster was full, so no call wrote the last slot");
        Assert.That(single.Clusters.Values.Count(c => BitOperations.PopCount(c.Pending) >= 2), Is.GreaterThan(0),
            "precondition: no cluster had two crossings in one tick");
        Assert.That(single.Clusters.Values.Count(c => c.Shrink != 0), Is.GreaterThan(0), "precondition: no write moved an extreme inward");
        Assert.That(single.Absorbed, Is.GreaterThan(0), "precondition: no write landed in the hysteresis band");

        AssertSameAsSingleWrites(single, set);
        Assert.That(set.CellOf, Is.EquivalentTo(single.CellOf),
            $"{Ca03Marker}: after the fence an entity sits in another cell than the single writes left it in");
        Assert.That(single.Ca01Violations, Is.Zero, "CA-01 failed after the single writes' fence");
        Assert.That(set.Ca01Violations, Is.Zero, $"{Ca03Marker}: CA-01 failed after the set write's fence");
    }

    /// <summary>
    /// Every other entity of each cluster written back unchanged, by a partial mask: nothing grows, nothing moves inward, nothing crosses, so the single
    /// writes set no flag, and the set write must set none either — the process bit included.
    /// </summary>
    [TestCase(Tier.Aabb2F)]
    [TestCase(Tier.Aabb3F)]
    [TestCase(Tier.BSphere2F)]
    [TestCase(Tier.BSphere3F)]
    [TestCase(Tier.Aabb2D)]
    [TestCase(Tier.Aabb3D)]
    [TestCase(Tier.BSphere2D)]
    [TestCase(Tier.BSphere3D)]
    [VerifiesRule("CA-03")]
    public void ASetWriteThatChangesNothing_SetsNoProcessBit(Tier tier)
    {
        var single = Run(tier, setWrite: false, Motion.RewriteEveryOther);
        var set = Run(tier, setWrite: true, Motion.RewriteEveryOther);

        Assert.That(set.Layout, Is.EqualTo(single.Layout), "precondition: the two engines built different clusters from one seed");
        Assert.That(single.Clusters.Values.Any(c => c.Process || c.Shrink != 0 || c.Pending != 0), Is.False,
            "precondition: a slot written back unchanged set a flag through the single write");

        AssertSameAsSingleWrites(single, set);
        Assert.That(set.Ca01Violations, Is.Zero, $"{Ca03Marker}: CA-01 failed after the set write's fence");
    }

    /// <summary>
    /// A slot whose position is not finite stops the call where the single write throws: the slot before it and that slot's own grow are published as the
    /// single writes publish them, the slot after it is not written, and the exception is the single write's.
    /// </summary>
    [Test]
    [VerifiesRule("CA-03")]
    public void ANonFinitePosition_PublishesTheSlotsBeforeIt()
    {
        var single = RunNonFinite(setWrite: false, out var singleThrown);
        var set = RunNonFinite(setWrite: true, out var setThrown);

        Assert.That(singleThrown, Is.Not.Null, "precondition: the single write accepted an infinite position");
        Assert.That(single.Clusters.Values.Single().Process, Is.True, "precondition: slot 0's grow should have set the process bit");
        Assert.That(setThrown?.GetType(), Is.EqualTo(singleThrown.GetType()),
            $"{Ca03Marker}: the set write did not throw the single write's exception (got {setThrown?.GetType().Name ?? "no exception"})");
        AssertSameAsSingleWrites(single, set);
    }

    /// <summary>
    /// A slot beyond the values given, or beyond the cluster, is refused before anything is written. On a tier whose cluster holds fewer than 64 slots, so
    /// that a slot beyond it can be named at all.
    /// </summary>
    [Test]
    [VerifiesRule("CA-03")]
    public void ASlotBeyondTheValuesOrTheCluster_IsRefused()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine<SetWA3D>(scope);
        dbe.SetSpatialBarrierOnly<SetWA3DUnit>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                tx.Spawn<SetWA3DUnit>(SetWA3DUnit.Box.Set(A3D(50 + i, 50, 50)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var checkedClusters = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<SetWA3DUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    Assert.That(cluster.ClusterSize, Is.LessThan(64), "precondition: this tier's cluster should hold fewer than 64 slots");
                    var before = cluster.GetReadOnlySpan(SetWA3DUnit.Box)[0].Box;
                    var far = A3D(700, 700, 700);

                    // Slot 2 is beyond the two values; then a slot beyond the cluster, with values for all 64. Slot 0 is in both masks and must not have
                    // been written when the call is refused. Any exception is caught, so one thrown from inside the loop fails the assertion rather than
                    // the test harness.
                    Exception refusedShort = null;
                    try
                    {
                        cluster.WriteSpatial(SetWA3DUnit.Box, 0b101UL, new[] { far, far });
                    }
                    catch (Exception e)
                    {
                        refusedShort = e;
                    }

                    Assert.That(refusedShort, Is.InstanceOf<ArgumentException>(),
                        $"{Ca03Marker}: a slot beyond the values given was not refused up front (got {refusedShort?.GetType().Name ?? "no exception"})");
                    AssertUnchanged(before, cluster.GetReadOnlySpan(SetWA3DUnit.Box)[0].Box, "a slot beyond the values");

                    Exception refusedWide = null;
                    try
                    {
                        cluster.WriteSpatial(SetWA3DUnit.Box, 1UL | (1UL << cluster.ClusterSize), Enumerable.Repeat(far, 64).ToArray());
                    }
                    catch (Exception e)
                    {
                        refusedWide = e;
                    }

                    Assert.That(refusedWide, Is.InstanceOf<ArgumentException>(),
                        $"{Ca03Marker}: a slot beyond the cluster was not refused up front (got {refusedWide?.GetType().Name ?? "no exception"})");
                    AssertUnchanged(before, cluster.GetReadOnlySpan(SetWA3DUnit.Box)[0].Box, "a slot beyond the cluster");

                    checkedClusters++;
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        Assert.That(checkedClusters, Is.GreaterThan(0), "precondition: no cluster was checked");
    }

    private static void AssertUnchanged(AABB3D before, AABB3D after, string what) =>
        Assert.That((after.MinX, after.MinY, after.MinZ, after.MaxX, after.MaxY, after.MaxZ),
            Is.EqualTo((before.MinX, before.MinY, before.MinZ, before.MaxX, before.MaxY, before.MaxZ)),
            $"{Ca03Marker}: the call refused for {what} wrote slot 0 before refusing");

    private static void AssertSameAsSingleWrites(Arm single, Arm set)
    {
        foreach (var (chunkId, s) in single.Clusters)
        {
            var b = set.Clusters[chunkId];
            Assert.That(set.Values[chunkId], Is.EqualTo(single.Values[chunkId]), $"{Ca03Marker}: cluster {chunkId}'s slot values differ from the single writes'");
            Assert.That((b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ), Is.EqualTo((s.MinX, s.MinY, s.MinZ, s.MaxX, s.MaxY, s.MaxZ)),
                $"{Ca03Marker}: cluster {chunkId}'s bound differs from the single writes'");
            Assert.That(b.Pending, Is.EqualTo(s.Pending), $"{Ca03Marker}: cluster {chunkId}'s migration flags differ from the single writes'");
            if (s.Pending != 0)
            {
                Assert.That(b.DestKey, Is.EqualTo(s.DestKey), $"{Ca03Marker}: cluster {chunkId}'s destination hint differs from the single writes'");
            }

            Assert.That(b.Shrink, Is.EqualTo(s.Shrink), $"{Ca03Marker}: cluster {chunkId}'s shrink flags differ from the single writes'");
            Assert.That(b.Process, Is.EqualTo(s.Process), $"{Ca03Marker}: cluster {chunkId}'s process bit differs from the single writes'");
        }

        Assert.That(set.MigrationHint, Is.EqualTo(single.MigrationHint), $"{Ca03Marker}: MigrationHint differs from the single writes'");
        Assert.That(set.Absorbed, Is.EqualTo(single.Absorbed), $"{Ca03Marker}: HysteresisAbsorbedLive differs from the single writes'");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // One arm
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private readonly record struct Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    private readonly record struct Snapshot(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ, ulong Pending, int DestKey, byte Shrink,
        bool Process);

    private sealed class Arm
    {
        public readonly SortedDictionary<int, ulong> Layout = new();
        public readonly SortedDictionary<int, Snapshot> Clusters = new();
        public readonly SortedDictionary<int, byte[]> Values = new();
        public readonly Dictionary<ulong, int> CellOf = new();
        public ulong FullMask;
        public int MigrationHint;
        public int Absorbed;
        public int Ca01Violations;
    }

    private static SetWA2F A2F(double x, double y, double z) =>
        new() { Box = new AABB2F { MinX = (float)(x - 1), MinY = (float)(y - 1), MaxX = (float)(x + 1), MaxY = (float)(y + 1) } };

    private static SetWA3D A3D(double x, double y, double z) =>
        new() { Box = new AABB3D { MinX = x - 1, MinY = y - 1, MinZ = z - 1, MaxX = x + 1, MaxY = y + 1, MaxZ = z + 1 } };

    private Arm Run(Tier tier, bool setWrite, Motion motion) => tier switch
    {
        Tier.Aabb2F => RunArm<SetWA2FUnit, SetWA2F>(SetWA2FUnit.Box, A2F,
            v => new Box(v.Box.MinX, v.Box.MinY, 0, v.Box.MaxX, v.Box.MaxY, 0), is3D: false, setWrite, motion),
        Tier.Aabb3F => RunArm<SetWA3FUnit, SetWA3F>(SetWA3FUnit.Box,
            (x, y, z) => new SetWA3F
            {
                Box = new AABB3F
                {
                    MinX = (float)(x - 1), MinY = (float)(y - 1), MinZ = (float)(z - 1), MaxX = (float)(x + 1), MaxY = (float)(y + 1), MaxZ = (float)(z + 1),
                },
            },
            v => new Box(v.Box.MinX, v.Box.MinY, v.Box.MinZ, v.Box.MaxX, v.Box.MaxY, v.Box.MaxZ), is3D: true, setWrite, motion),
        Tier.BSphere2F => RunArm<SetWS2FUnit, SetWS2F>(SetWS2FUnit.Ball,
            (x, y, _) => new SetWS2F { Ball = new BSphere2F { CenterX = (float)x, CenterY = (float)y, Radius = 1.5f } },
            v =>
            {
                var e = SpatialGeometry.Enclosing(v.Ball);
                return new Box(e.MinX, e.MinY, 0, e.MaxX, e.MaxY, 0);
            }, is3D: false, setWrite, motion),
        Tier.BSphere3F => RunArm<SetWS3FUnit, SetWS3F>(SetWS3FUnit.Ball,
            (x, y, z) => new SetWS3F { Ball = new BSphere3F { CenterX = (float)x, CenterY = (float)y, CenterZ = (float)z, Radius = 1.5f } },
            v =>
            {
                var e = SpatialGeometry.Enclosing(v.Ball);
                return new Box(e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ);
            }, is3D: true, setWrite, motion),
        Tier.Aabb2D => RunArm<SetWA2DUnit, SetWA2D>(SetWA2DUnit.Box,
            (x, y, _) => new SetWA2D { Box = new AABB2D { MinX = x - 1, MinY = y - 1, MaxX = x + 1, MaxY = y + 1 } },
            v => new Box(v.Box.MinX, v.Box.MinY, 0, v.Box.MaxX, v.Box.MaxY, 0), is3D: false, setWrite, motion),
        Tier.Aabb3D => RunArm<SetWA3DUnit, SetWA3D>(SetWA3DUnit.Box, A3D,
            v => new Box(v.Box.MinX, v.Box.MinY, v.Box.MinZ, v.Box.MaxX, v.Box.MaxY, v.Box.MaxZ), is3D: true, setWrite, motion),
        Tier.BSphere2D => RunArm<SetWS2DUnit, SetWS2D>(SetWS2DUnit.Ball,
            (x, y, _) => new SetWS2D { Ball = new BSphere2D { CenterX = x, CenterY = y, Radius = 1.5 } },
            v =>
            {
                var e = SpatialGeometry.Enclosing(v.Ball);
                return new Box(e.MinX, e.MinY, 0, e.MaxX, e.MaxY, 0);
            }, is3D: false, setWrite, motion),
        Tier.BSphere3D => RunArm<SetWS3DUnit, SetWS3D>(SetWS3DUnit.Ball,
            (x, y, z) => new SetWS3D { Ball = new BSphere3D { CenterX = x, CenterY = y, CenterZ = z, Radius = 1.5 } },
            v =>
            {
                var e = SpatialGeometry.Enclosing(v.Ball);
                return new Box(e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ);
            }, is3D: true, setWrite, motion),
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    private DatabaseEngine SetupEngine<T>(IServiceScope scope) where T : unmanaged
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<T>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector3D(0, 0, 0), new Vector3D(WorldMax, WorldMax, WorldMax), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawns the population and a dense block, fences, writes every cluster once — slot by slot or one call per cluster — and records the cluster state
    /// before the next fence and the entities' cells and CA-01 after it. A fresh database per arm: both would otherwise open the same file.
    /// </summary>
    private Arm RunArm<TArch, T>(Comp<T> comp, Func<double, double, double, T> make, Func<T, Box> boxOf, bool is3D, bool setWrite, Motion motion)
        where TArch : Archetype<TArch> where T : unmanaged
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine<T>(scope);
        dbe.SetSpatialBarrierOnly<TArch>();

        var rng = new Random(Seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = SpawnLow + rng.NextDouble() * (SpawnHigh - SpawnLow);
                var y = SpawnLow + rng.NextDouble() * (SpawnHigh - SpawnLow);
                var z = is3D ? SpawnLow + rng.NextDouble() * (SpawnHigh - SpawnLow) : 0;
                tx.Spawn<TArch>(comp.Set(make(x, y, z)));
            }

            for (var i = 0; i < DenseCount; i++)
            {
                var x = DenseCentre + (rng.NextDouble() * 2 - 1) * 4;
                var y = DenseCentre + (rng.NextDouble() * 2 - 1) * 4;
                var z = is3D ? DenseCentre + (rng.NextDouble() * 2 - 1) * 4 : 0;
                tx.Spawn<TArch>(comp.Set(make(x, y, z)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var state = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<TArch>().ArchetypeId].ClusterState;
        var arm = new Arm();
        var values = new T[64];
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<TArch>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var occupied = cluster.OccupancyBits;
                    arm.Layout[cluster.ChunkId] = occupied;
                    arm.FullMask = cluster.ClusterSize == 64 ? ulong.MaxValue : (1UL << cluster.ClusterSize) - 1;
                    var current = cluster.GetReadOnlySpan(comp);
                    var mask = 0UL;
                    var nth = 0;
                    for (var bits = occupied; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        if (motion == Motion.RewriteEveryOther && (nth++ & 1) != 0)
                        {
                            continue;
                        }

                        values[slot] = motion == Motion.MoveAll
                            ? make(Moved(current[slot], boxOf, cluster.ChunkId, slot, is3D, out var y, out var z), y, z)
                            : current[slot];
                        mask |= 1UL << slot;
                    }

                    if (setWrite)
                    {
                        cluster.WriteSpatial(comp, mask, values);
                    }
                    else
                    {
                        for (var bits = mask; bits != 0; bits &= bits - 1)
                        {
                            var slot = BitOperations.TrailingZeroCount(bits);
                            cluster.WriteSpatial(comp, slot, values[slot]);
                        }
                    }

                    arm.Values[cluster.ChunkId] = OccupiedBytes(cluster.GetReadOnlySpan(comp), occupied);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        Record(state, arm);

        dbe.WriteTickFence(2);

        var grid = dbe.SpatialGrid;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<TArch>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var cellKey = state.ClusterCellMap[cluster.ChunkId];
                    var stored = state.ClusterAabbs[cluster.ChunkId];
                    grid.CellOrigin(cellKey, out double ox, out double oy, out double oz);
                    var current = cluster.GetReadOnlySpan(comp);
                    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        arm.CellOf[cluster.GetEntityId(slot).RawValue] = cellKey;
                        var box = boxOf(current[slot]);
                        var contained = stored.MinX <= ClusterSpatialAabb.ToCellRelativeMin(box.MinX, ox)
                                        && stored.MinY <= ClusterSpatialAabb.ToCellRelativeMin(box.MinY, oy)
                                        && stored.MaxX >= ClusterSpatialAabb.ToCellRelativeMax(box.MaxX, ox)
                                        && stored.MaxY >= ClusterSpatialAabb.ToCellRelativeMax(box.MaxY, oy)
                                        && (!is3D || (stored.MinZ <= ClusterSpatialAabb.ToCellRelativeMin(box.MinZ, oz)
                                                      && stored.MaxZ >= ClusterSpatialAabb.ToCellRelativeMax(box.MaxZ, oz)));
                        if (!contained)
                        {
                            arm.Ca01Violations++;
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        return arm;
    }

    /// <summary>
    /// Three entities in one cluster, then a write tick where slot 0 moves outward (a grow), slot 1's box reaches +Infinity (a centre WorldToCellKey
    /// refuses) and slot 2 moves: slot by slot, stopping at the throw as a caller's loop would, or as one call. No fence follows.
    /// </summary>
    private Arm RunNonFinite(bool setWrite, out Exception thrown)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine<SetWA2F>(scope);
        dbe.SetSpatialBarrierOnly<SetWA2FUnit>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                tx.Spawn<SetWA2FUnit>(SetWA2FUnit.Box.Set(A2F(50 + 2 * i, 50 + 2 * i, 0)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var state = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<SetWA2FUnit>().ArchetypeId].ClusterState;
        var arm = new Arm();
        var values = new[]
        {
            A2F(30, 30, 0),
            new SetWA2F { Box = new AABB2F { MinX = 51, MinY = 51, MaxX = float.PositiveInfinity, MaxY = 53 } },
            A2F(60, 60, 0),
        };

        thrown = null;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<SetWA2FUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    Assert.That(cluster.OccupancyBits, Is.EqualTo(0b111UL), "precondition: the three entities should share one cluster, in slots 0 to 2");
                    arm.Layout[cluster.ChunkId] = cluster.OccupancyBits;
                    try
                    {
                        if (setWrite)
                        {
                            cluster.WriteSpatial(SetWA2FUnit.Box, 0b111UL, values);
                        }
                        else
                        {
                            for (var slot = 0; slot < values.Length; slot++)
                            {
                                cluster.WriteSpatial(SetWA2FUnit.Box, slot, values[slot]);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        thrown = e;
                    }

                    arm.Values[cluster.ChunkId] = OccupiedBytes(cluster.GetReadOnlySpan(SetWA2FUnit.Box), cluster.OccupancyBits);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        Record(state, arm);
        return arm;
    }

    /// <summary>The cluster bookkeeping of every cluster in <see cref="Arm.Layout"/>, and the archetype's counters, as the write tick left them.</summary>
    private static void Record(ArchetypeClusterState state, Arm arm)
    {
        foreach (var chunkId in arm.Layout.Keys)
        {
            var b = state.ClusterAabbs[chunkId];
            var process = ((ulong)state.ClusterProcessBitmap[chunkId >> 6] >> (chunkId & 63) & 1) != 0;
            arm.Clusters[chunkId] = new Snapshot(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, state.ClusterMigrationPendingSlots[chunkId],
                state.ClusterMigrationDestCellKeys[chunkId], state.ClusterShrinkPendingAxes[chunkId], process);
        }

        arm.MigrationHint = state.MigrationHint;
        arm.Absorbed = state.HysteresisAbsorbedLive;
    }

    /// <summary>The bytes of the occupied slots, in slot order.</summary>
    private static byte[] OccupiedBytes<T>(ReadOnlySpan<T> span, ulong occupied) where T : unmanaged
    {
        var bytes = new List<byte>();
        for (var bits = occupied; bits != 0; bits &= bits - 1)
        {
            bytes.AddRange(MemoryMarshal.AsBytes(span.Slice(BitOperations.TrailingZeroCount(bits), 1)).ToArray());
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// The entity's new centre: a displacement drawn from its cluster and slot, so both arms move it the same way. A third of the moves stay near, a
    /// third go a few tens of units, a third cross cells; all stay in the world.
    /// </summary>
    private static double Moved<T>(T value, Func<T, Box> boxOf, int chunkId, int slot, bool is3D, out double y, out double z)
    {
        var box = boxOf(value);
        var rng = new Random(unchecked(Seed ^ (chunkId * 73856093) ^ (slot * 19349663)));
        var reach = rng.Next(3) switch
        {
            0 => 3.0,
            1 => 40.0,
            _ => 160.0,
        };

        var x = Math.Clamp(0.5 * (box.MinX + box.MaxX) + ((rng.NextDouble() * 2) - 1) * reach, 5, WorldMax - 5);
        y = Math.Clamp(0.5 * (box.MinY + box.MaxY) + ((rng.NextDouble() * 2) - 1) * reach, 5, WorldMax - 5);
        z = is3D ? Math.Clamp(0.5 * (box.MinZ + box.MaxZ) + ((rng.NextDouble() * 2) - 1) * reach, 5, WorldMax - 5) : 0;
        return x;
    }
}
