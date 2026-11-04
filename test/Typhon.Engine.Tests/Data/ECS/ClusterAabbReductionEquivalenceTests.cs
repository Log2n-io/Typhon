using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.AabbEq.A2F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AabbEqA2F
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class AabbEqUnitA2F : Archetype<AabbEqUnitA2F>
{
    public static readonly Comp<AabbEqA2F> Pos = Register<AabbEqA2F>();
}

[Component("Typhon.Test.AabbEq.A3F", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AabbEqA3F
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;
}

[Archetype]
partial class AabbEqUnitA3F : Archetype<AabbEqUnitA3F>
{
    public static readonly Comp<AabbEqA3F> Pos = Register<AabbEqA3F>();
}

[Component("Typhon.Test.AabbEq.A2D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AabbEqA2D
{
    [Field]
    [SpatialIndex]
    public AABB2D Bounds;
}

[Archetype]
partial class AabbEqUnitA2D : Archetype<AabbEqUnitA2D>
{
    public static readonly Comp<AabbEqA2D> Pos = Register<AabbEqA2D>();
}

[Component("Typhon.Test.AabbEq.S3D", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AabbEqS3D
{
    [Field]
    [SpatialIndex]
    public BSphere3D Ball;
}

[Archetype]
partial class AabbEqUnitS3D : Archetype<AabbEqUnitS3D>
{
    public static readonly Comp<AabbEqS3D> Ball = Register<AabbEqS3D>();
}

/// <summary>
/// CA-01 [fatal][silent]: the cluster AABB recompute — the tick fence's dirty and shrink passes and the rebuild — reduces the union in WORLD space and
/// converts it to the cell's frame once (<c>ReduceWorldBounds</c> + <c>ClusterWorldBounds.ToCellRelative</c>). These tests hold it bit for bit to the
/// formula it replaced: every valid entity converted with <c>ToCellRelativeMin/Max</c>, then unioned. Inputs are the ones a rounding difference would show
/// on: coordinates on and one ULP either side of cell edges, negative coordinates, extents, degenerate slots (NaN, inverted), and origins from zero to
/// 2^36, on f32 and f64 field tiers, 2D and 3D, the f32 AABB fast paths and the generic decoder.
/// </summary>
[TestFixture]
class ClusterAabbReductionEquivalenceTests : TestBase<ClusterAabbReductionEquivalenceTests>
{
    private const double Cell = 100;
    private const double Half = 800;   // world [-Half, Half] on every axis: negative coordinates included

    private static readonly double[] Origins = [0, -0.0, -Half, 1234.5678, -987.65432, 68_719_476_736, -1e9 + 0.3, 99.99999999999999];

    /// <summary>The adversarial coordinates: cell edges, one ULP (f32 or f64) either side, and points inside.</summary>
    private static double Coord(int i, bool f64)
    {
        var edge = -Half + Cell * (1 + i % 14);
        return (i % 6) switch
        {
            0 => edge,
            1 => f64 ? Math.BitDecrement(edge) : MathF.BitDecrement((float)edge),
            2 => f64 ? Math.BitIncrement(edge) : MathF.BitIncrement((float)edge),
            3 => edge + 0.1,
            4 => edge - 49.999,
            _ => edge + 33.3333333,
        };
    }

    private static double Extent(int i) => (i % 4) switch { 0 => 0, 1 => 0.001, 2 => 1.5, _ => 7.25 };

    [Test]
    [VerifiesRule("CA-01")]
    public void Aabb2F_FastPath_MatchesPerEntityConversion() =>
        Run<AabbEqUnitA2F, AabbEqA2F>(false, false, (tx, x, y, _, e) => tx.Spawn<AabbEqUnitA2F>(AabbEqUnitA2F.Pos.Set(new AabbEqA2F
        {
            Bounds = new AABB2F { MinX = (float)x, MinY = (float)y, MaxX = (float)(x + e), MaxY = (float)(y + e) },
        })));

    [Test]
    [VerifiesRule("CA-01")]
    public void Aabb3F_FastPath_MatchesPerEntityConversion() =>
        Run<AabbEqUnitA3F, AabbEqA3F>(true, false, (tx, x, y, z, e) => tx.Spawn<AabbEqUnitA3F>(AabbEqUnitA3F.Pos.Set(new AabbEqA3F
        {
            Bounds = new AABB3F
            {
                MinX = (float)x, MinY = (float)y, MinZ = (float)z, MaxX = (float)(x + e), MaxY = (float)(y + e), MaxZ = (float)(z + e),
            },
        })));

    [Test]
    [VerifiesRule("CA-01")]
    public void Aabb2D_GenericPath_MatchesPerEntityConversion() =>
        Run<AabbEqUnitA2D, AabbEqA2D>(false, true, (tx, x, y, _, e) => tx.Spawn<AabbEqUnitA2D>(AabbEqUnitA2D.Pos.Set(new AabbEqA2D
        {
            Bounds = new AABB2D { MinX = x, MinY = y, MaxX = x + e, MaxY = y + e },
        })));

    [Test]
    [VerifiesRule("CA-01")]
    public void BSphere3D_GenericPath_MatchesPerEntityConversion() =>
        Run<AabbEqUnitS3D, AabbEqS3D>(true, true, (tx, x, y, z, e) => tx.Spawn<AabbEqUnitS3D>(AabbEqUnitS3D.Ball.Set(new AabbEqS3D
        {
            Ball = new BSphere3D { CenterX = x, CenterY = y, CenterZ = z, Radius = e },
        })));

    /// <summary>
    /// The pure half: random world bounds near cell edges and huge origins, the union reduced in world space then converted, against every entity
    /// converted then unioned.
    /// </summary>
    [Test]
    [VerifiesRule("CA-01")]
    public void WorldReduction_ThenConvert_EqualsConvertThenUnion()
    {
        var rnd = new Random(1394);
        for (var trial = 0; trial < 4000; trial++)
        {
            var is3D = (trial & 1) == 0;
            var origin = Origins[trial % Origins.Length] + (trial % 3 == 0 ? rnd.NextDouble() : 0);
            var n = 1 + rnd.Next(64);
            var world = ClusterWorldBounds.Empty(is3D);
            var oracle = ClusterSpatialAabb.Empty;
            for (var i = 0; i < n; i++)
            {
                var c = new double[6];
                for (var a = 0; a < 3; a++)
                {
                    var v = origin + Coord(rnd.Next(1000), (trial & 2) != 0) + (trial % 5 == 0 ? rnd.NextDouble() * 1e-9 : 0);
                    if ((trial & 2) == 0)
                    {
                        v = (float)v;   // an f32 tier's value
                    }

                    c[a] = v;
                    c[a + 3] = v + Extent(rnd.Next(4));
                }

                if (is3D)
                {
                    world.Union3(c[0], c[1], c[2], c[3], c[4], c[5]);
                    oracle.Union3F(ClusterSpatialAabb.ToCellRelativeMin(c[0], origin), ClusterSpatialAabb.ToCellRelativeMin(c[1], origin),
                        ClusterSpatialAabb.ToCellRelativeMin(c[2], origin), ClusterSpatialAabb.ToCellRelativeMax(c[3], origin),
                        ClusterSpatialAabb.ToCellRelativeMax(c[4], origin), ClusterSpatialAabb.ToCellRelativeMax(c[5], origin), 7u);
                }
                else
                {
                    world.Union2(c[0], c[1], c[3], c[4]);
                    oracle.Union2F(ClusterSpatialAabb.ToCellRelativeMin(c[0], origin), ClusterSpatialAabb.ToCellRelativeMin(c[1], origin),
                        ClusterSpatialAabb.ToCellRelativeMax(c[3], origin), ClusterSpatialAabb.ToCellRelativeMax(c[4], origin), 7u);
                }
            }

            AssertBitIdentical(world.ToCellRelative(origin, origin, origin, 7u), oracle, $"trial {trial}, origin {origin:R}, 3D {is3D}");
        }
    }

    private delegate void Spawner(Transaction tx, double x, double y, double z, double extent);

    private unsafe void Run<TArch, TComp>(bool is3D, bool f64, Spawner spawn) where TArch : Archetype<TArch>, new() where TComp : unmanaged
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TComp>();
        dbe.ConfigureSpatialGrid(is3D
            ? new SpatialGridConfig(new Vector3D(-Half, -Half, -Half), new Vector3D(Half, Half, Half), Cell)
            : SpatialGridConfig.Flat(new Vector2((float)-Half, (float)-Half), new Vector2((float)Half, (float)Half), Cell));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 600; i++)
            {
                spawn(tx, Coord(i, f64), Coord(i * 7 + 3, f64), Coord(i * 13 + 5, f64), Extent(i));
            }

            tx.Commit();
        }

        var cs = dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;
        ref readonly var ss = ref cs.SpatialSlot;
        var stride = cs.Layout.ComponentSize(ss.Slot);
        var fieldOffset = cs.Layout.ComponentOffset(ss.Slot) + ss.FieldOffset;
        var maxIndex = is3D ? 3 : 2;   // index of MaxX in an AABB layout
        var isAabb = ss.FieldInfo.FieldType is SpatialFieldType.AABB2F or SpatialFieldType.AABB3F or SpatialFieldType.AABB2D or SpatialFieldType.AABB3D;
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "precondition: several clusters");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            Span<double> coords = stackalloc double[6];
            var degenerate = 0;
            for (var c = 0; c < cs.ActiveClusterCount; c++)
            {
                var chunkId = cs.ActiveClusterIds[c];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;

                // Degenerate slots, written into the cluster's bytes: a NaN, and (AABB tiers) an inverted box. Both must be skipped alike.
                for (var bits = occupancy; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var field = clusterBase + fieldOffset + slot * stride;
                    if (slot % 9 == 4)
                    {
                        if (f64) { *(double*)field = double.NaN; } else { *(float*)field = float.NaN; }
                        degenerate++;
                    }
                    else if (slot % 9 == 7 && isAabb)
                    {
                        if (f64) { *(double*)field = ((double*)field)[maxIndex] + 1; } else { *(float*)field = ((float*)field)[maxIndex] + 1; }
                        degenerate++;
                    }
                }

                foreach (var o in Origins)
                {
                    foreach (var (ox, oy, oz) in new[] { (o, o, o), (o, -o + 0.5, o * 0.25) })
                    {
                        var actual = cs.RecomputeClusterAabb(chunkId, ref accessor, ox, oy, oz, out var scanned);
                        Assert.That(scanned, Is.EqualTo(System.Numerics.BitOperations.PopCount(occupancy)));

                        // The pre-change formula: each valid entity converted, then unioned.
                        var oracle = ClusterSpatialAabb.Empty;
                        for (var bits = occupancy; bits != 0; bits &= bits - 1)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(clusterBase + fieldOffset + slot * stride, ss.FieldInfo, coords))
                            {
                                continue;
                            }

                            if (is3D)
                            {
                                oracle.Union3F(ClusterSpatialAabb.ToCellRelativeMin(coords[0], ox), ClusterSpatialAabb.ToCellRelativeMin(coords[1], oy),
                                    ClusterSpatialAabb.ToCellRelativeMin(coords[2], oz), ClusterSpatialAabb.ToCellRelativeMax(coords[3], ox),
                                    ClusterSpatialAabb.ToCellRelativeMax(coords[4], oy), ClusterSpatialAabb.ToCellRelativeMax(coords[5], oz),
                                    ss.FieldInfo.Category);
                            }
                            else
                            {
                                oracle.Union2F(ClusterSpatialAabb.ToCellRelativeMin(coords[0], ox), ClusterSpatialAabb.ToCellRelativeMin(coords[1], oy),
                                    ClusterSpatialAabb.ToCellRelativeMax(coords[2], ox), ClusterSpatialAabb.ToCellRelativeMax(coords[3], oy),
                                    ss.FieldInfo.Category);
                            }
                        }

                        AssertBitIdentical(actual, oracle, $"cluster {chunkId}, origin ({ox:R}, {oy:R}, {oz:R})");
                    }
                }
            }

            Assert.That(degenerate, Is.GreaterThan(0), "precondition: degenerate slots exercised");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static void AssertBitIdentical(in ClusterSpatialAabb actual, in ClusterSpatialAabb expected, string where)
    {
        if (Bits(actual) != Bits(expected))
        {
            Assert.Fail($"{where}: got {Describe(actual)}, expected {Describe(expected)}");
        }
    }

    private static string Describe(in ClusterSpatialAabb a) =>
        $"[{a.MinX:R} {a.MinY:R} {a.MinZ:R} .. {a.MaxX:R} {a.MaxY:R} {a.MaxZ:R} | {a.CategoryMask}]";

    private static (int, int, int, int, int, int, uint) Bits(in ClusterSpatialAabb a) =>
        (BitConverter.SingleToInt32Bits(a.MinX), BitConverter.SingleToInt32Bits(a.MinY), BitConverter.SingleToInt32Bits(a.MinZ),
            BitConverter.SingleToInt32Bits(a.MaxX), BitConverter.SingleToInt32Bits(a.MaxY), BitConverter.SingleToInt32Bits(a.MaxZ), a.CategoryMask);
}
