using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ClTail.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClTailPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

/// <summary>Twenty bytes of nothing, sized so the archetype's cluster holds 59 slots — a size that is not a multiple of the kernel's block.</summary>
[Component("Typhon.Test.ClTail.Filler", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClTailFiller
{
    [Field] public int A;
    [Field] public int B;
    [Field] public int C;
    [Field] public int D;
    [Field] public int E;
}

[Archetype]
partial class ClTailUnit : Archetype<ClTailUnit>
{
    public static readonly Comp<ClTailPos> Pos = Register<ClTailPos>();
    public static readonly Comp<ClTailFiller> Filler = Register<ClTailFiller>();
}

/// <summary>
/// <see cref="AabbClusterEnumerator.Count"/>, <see cref="AabbClusterEnumerator.Fill"/> and <see cref="AabbClusterEnumerator.MoveNext"/>: the three drains must
/// answer what an oracle computed from the spawned bounds answers — the same set, the same bounds and squared distances bit for bit — and must agree with
/// each other in order, on every broadphase path (scalar scan, batched scan past one 64-slot batch, a promoted cell's tree) and every storage tier.
/// </summary>
[TestFixture]
unsafe class AabbClusterEnumeratorDrainTests : TestBase<AabbClusterEnumeratorDrainTests>
{
    // ── Queries, and the oracle that answers them from the spawned bounds ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A query shape in world doubles. 2D queries carry ±Infinity on Z, as <see cref="ClusterSpatialQuery{TArch}"/> passes for a 2D archetype.
    /// </summary>
    private readonly record struct Q(bool Is3D, bool IsRadius, double X0, double Y0, double Z0, double X1, double Y1, double Z1, double R)
    {
        public static Q Box2D(double x0, double y0, double x1, double y1) => new(false, false, x0, y0, 0d, x1, y1, 0d, 0d);

        public static Q Sphere2D(double cx, double cy, double r) => new(false, true, cx, cy, 0d, 0d, 0d, 0d, r);
    }

    /// <summary>
    /// One spawned entity's tight bounds as the engine reads them: stored floats widened (exact), or the f64 values, or a sphere's enclosing box.
    /// </summary>
    private readonly record struct Spawned(long Id, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    private static AabbClusterEnumerator Open(DatabaseEngine dbe, ArchetypeClusterState cs, Q q) =>
        q.IsRadius
            ? cs.QueryRadius(dbe.Realm0Grid, q.X0, q.Y0, q.Z0, q.R)
            : cs.QueryAabb(dbe.Realm0Grid, q.X0, q.Y0, q.Is3D ? q.Z0 : double.NegativeInfinity, q.X1, q.Y1, q.Is3D ? q.Z1 : double.PositiveInfinity);

    /// <summary>
    /// What the query must return, computed the way the narrowphase is specified: box overlap on every axis, then for a radius query the squared distance
    /// from the centre to the closest point of the box, <c>c - Clamp(c, min, max)</c> per axis — the reference form the branch-free drain must reproduce.
    /// </summary>
    private static Dictionary<long, ClusterSpatialQueryResult> Oracle(List<Spawned> population, Q q)
    {
        double qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ;
        if (q.IsRadius)
        {
            (qMinX, qMinY, qMaxX, qMaxY) = (q.X0 - q.R, q.Y0 - q.R, q.X0 + q.R, q.Y0 + q.R);
            (qMinZ, qMaxZ) = q.Is3D ? (q.Z0 - q.R, q.Z0 + q.R) : (double.NegativeInfinity, double.PositiveInfinity);
        }
        else
        {
            (qMinX, qMinY, qMaxX, qMaxY) = (q.X0, q.Y0, q.X1, q.Y1);
            (qMinZ, qMaxZ) = q.Is3D ? (q.Z0, q.Z1) : (double.NegativeInfinity, double.PositiveInfinity);
        }

        var expected = new Dictionary<long, ClusterSpatialQueryResult>();
        foreach (var s in population)
        {
            double minZ = q.Is3D ? s.MinZ : qMinZ;
            double maxZ = q.Is3D ? s.MaxZ : qMaxZ;
            if (s.MaxX < qMinX || s.MinX > qMaxX || s.MaxY < qMinY || s.MinY > qMaxY || maxZ < qMinZ || minZ > qMaxZ)
            {
                continue;
            }

            double distSq = 0d;
            if (q.IsRadius)
            {
                double cz = q.Is3D ? q.Z0 : 0d;
                double dx = q.X0 - Math.Clamp(q.X0, s.MinX, s.MaxX);
                double dy = q.Y0 - Math.Clamp(q.Y0, s.MinY, s.MaxY);
                double dz = cz - Math.Clamp(cz, minZ, maxZ);
                distSq = (dx * dx) + (dy * dy) + (dz * dz);
                if (distSq > q.R * q.R)
                {
                    continue;
                }
            }

            expected[s.Id] = new ClusterSpatialQueryResult(EntityId.FromRaw(s.Id), 0, 0, s.MinX, s.MinY, minZ, s.MaxX, s.MaxY, maxZ, distSq);
        }

        return expected;
    }

    /// <summary>
    /// The result struct's size is part of its contract: it is filled into caller-supplied spans by <see cref="AabbClusterEnumerator.Fill"/> and copied once
    /// per iteration, so a member that silently widens it is a per-hit cost nothing else would catch.
    /// </summary>
    /// <remarks>
    /// Asserted because #909 retyped <c>EntityId</c> (a raw <c>long</c>) to <c>Entity</c> (an <see cref="EntityId"/>), and the whole case for doing so rests on
    /// the replacement being the same 8 bytes in the same position. Nothing in the repository asserted this before, which is exactly why it was worth adding
    /// with the change that depends on it.
    /// </remarks>
    [Test]
    public void TheResultStructIsUnchangedInSize()
    {
        Assert.That(System.Runtime.CompilerServices.Unsafe.SizeOf<ClusterSpatialQueryResult>(), Is.EqualTo(72),
            "ClusterSpatialQueryResult must stay 72 bytes: EntityId is Size = 8, exactly like the long it replaced");
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>
    /// Run <paramref name="q"/> through MoveNext, Count, and Fill at three buffer sizes; assert MoveNext matches the oracle and the other two match MoveNext in
    /// order; return the hit count.
    /// </summary>
    private static int AssertDrains(DatabaseEngine dbe, ArchetypeClusterState cs, List<Spawned> population, Q q, string what)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        var actual = new List<ClusterSpatialQueryResult>();
        var e = Open(dbe, cs, q);
        try
        {
            while (e.MoveNext())
            {
                actual.Add(e.Current);
            }
        }
        finally
        {
            e.Dispose();
        }

        var expected = Oracle(population, q);
        var missing = expected.Keys.Except(actual.Select(r => unchecked((long)r.Entity.RawValue))).ToList();
        var extra = actual.Select(r => unchecked((long)r.Entity.RawValue)).Except(expected.Keys).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            var byId = population.ToDictionary(s => s.Id);
            string Show(long id)
            {
                if (!byId.TryGetValue(id, out var s))
                {
                    return $"{id} (not spawned)";
                }
                int home = dbe.Realm0Grid.WorldToCellKey((s.MinX + s.MaxX) / 2, (s.MinY + s.MaxY) / 2, (s.MinZ + s.MaxZ) / 2);
                return $"{id} [{s.MinX}, {s.MinY}, {s.MinZ}]..[{s.MaxX}, {s.MaxY}, {s.MaxZ}] home cell {home}";
            }
            Assert.Fail($"{what}: MoveNext's set differs from the oracle's for {q}. Missing: {string.Join("; ", missing.Select(Show))}. "
                + $"Extra: {string.Join("; ", extra.Select(Show))}.");
        }
        foreach (var r in actual)
        {
            var x = expected[unchecked((long)r.Entity.RawValue)];
            Assert.That(SameBits(r.MinX, x.MinX) && SameBits(r.MinY, x.MinY) && SameBits(r.MinZ, x.MinZ) && SameBits(r.MaxX, x.MaxX)
                        && SameBits(r.MaxY, x.MaxY) && SameBits(r.MaxZ, x.MaxZ) && SameBits(r.DistanceSq, x.DistanceSq),
                Is.True, $"{what}: entity {r.Entity} came back as {Describe(r)}, the oracle says {Describe(x)}, for {q}");
        }

        e = Open(dbe, cs, q);
        try
        {
            Assert.That(e.Count(), Is.EqualTo(actual.Count), $"{what}: Count() disagrees with MoveNext for {q}");
        }
        finally
        {
            e.Dispose();
        }

        foreach (var size in new[] { 1, 7, 64 })
        {
            var buffer = new ClusterSpatialQueryResult[size];
            var filled = new List<ClusterSpatialQueryResult>();
            e = Open(dbe, cs, q);
            try
            {
                int n;
                while ((n = e.Fill(buffer)) > 0)
                {
                    filled.AddRange(buffer.AsSpan(0, n).ToArray());
                }
            }
            finally
            {
                e.Dispose();
            }

            Assert.That(filled, Is.EqualTo(actual), $"{what}: Fill into {size} slots disagrees with MoveNext for {q}");
        }

        return actual.Count;
    }

    private static string Describe(ClusterSpatialQueryResult r) =>
        $"[{r.MinX}, {r.MinY}, {r.MinZ}]..[{r.MaxX}, {r.MaxY}, {r.MaxZ}] d²={r.DistanceSq}";

    private static IEnumerable<Q> RandomQueries2D(double extent, int seed, int count)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            double x = rng.NextDouble() * extent;
            double y = rng.NextDouble() * extent;
            yield return Q.Box2D(x, y, x + 10d + (rng.NextDouble() * extent * 0.4), y + 10d + (rng.NextDouble() * extent * 0.4));
            // Radii are floats in BSphere2F; keep them float-exact so the engine and the oracle start from the same double.
            yield return Q.Sphere2D((float)x, (float)y, (float)(5d + (rng.NextDouble() * extent * 0.3)));
        }
    }

    // ── The AABB2F archetype, through each broadphase path ────────────────────────────────────────────────────────────────────────────────────────────

    private DatabaseEngine SetupEngine(float cellSize, float worldMax, int promoteThreshold = 0)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(worldMax, worldMax), cellSize));
        if (promoteThreshold > 0)
        {
            // Count-only promotion: these clusters are scattered over the cell on purpose, the shape the tightness gate refuses.
            dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
            dbe.ClusterCellTreePromoteTightness = 1f;
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn inside [origin + 5, origin + extent - 5]²: a third points, the rest boxes up to 10 wide, so the narrowphase sees both shapes.</summary>
    private static List<Spawned> Spawn(DatabaseEngine dbe, int count, float origin, float extent, int seed)
    {
        var rng = new Random(seed);
        var population = new List<Spawned>(count);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                float x = origin + 5f + ((float)rng.NextDouble() * (extent - 10f));
                float y = origin + 5f + ((float)rng.NextDouble() * (extent - 10f));
                float half = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 5f;
                var b = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
                var pos = new ClCohPos { Bounds = b, Mass = 1f };
                var id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos));
                population.Add(new Spawned((long)id.RawValue, b.MinX, b.MinY, 0d, b.MaxX, b.MaxY, 0d));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return population;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    [Test]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    public void Drains_MatchTheOracle_OnAScatteredPopulation()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        var population = Spawn(dbe, 2_000, origin: 0f, extent: 1_000f, seed: 1);
        var cs = ClusterStateOf(dbe);

        var total = RandomQueries2D(1_000d, seed: 2, count: 30).Sum(q => AssertDrains(dbe, cs, population, q, "scattered"));

        Assert.That(total, Is.GreaterThan(1_000), "the queries must hit something, or identical empty answers pass trivially");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    public void Drains_MatchTheOracle_WhenACellHalfNeedsSeveralBroadphaseBatches()
    {
        using var dbe = SetupEngine(cellSize: 1_000f, worldMax: 4_000f);
        var population = Spawn(dbe, 4_800, origin: 0f, extent: 1_000f, seed: 3);
        var cs = ClusterStateOf(dbe);

        var cellKey = dbe.Realm0Grid.WorldToCellKey(500d, 500d, 0d);
        Assert.Multiple(() =>
        {
            Assert.That(SpatialQueryTuning.SimdLinearScan, Is.True, "precondition: the batched scan must be enabled, or this test runs the scalar one");
            Assert.That(cs.Realm0Spatial.PerCellIndex[cellKey].DynamicClusterCount, Is.GreaterThan(64),
                "precondition: the cell half must need more than one 64-slot batch");
        });

        var total = RandomQueries2D(1_000d, seed: 4, count: 12).Sum(q => AssertDrains(dbe, cs, population, q, "batched"));

        Assert.That(total, Is.GreaterThan(1_000), "the queries must hit something, or identical empty answers pass trivially");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    public void Drains_MatchTheOracle_OverAPromotedCell()
    {
        using var dbe = SetupEngine(cellSize: 1_000f, worldMax: 4_000f, promoteThreshold: 24);
        var population = Spawn(dbe, 3_000, origin: 0f, extent: 1_000f, seed: 5);
        var cs = ClusterStateOf(dbe);

        Assert.That(cs.Realm0Spatial.PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote, or the tree path never runs");

        // Boxes straddling the populated cell and its empty neighbours too, so the cell walk leaves a tree half for a cell with nothing in it.
        var total = RandomQueries2D(1_400d, seed: 6, count: 12).Sum(q => AssertDrains(dbe, cs, population, q, "promoted"));

        Assert.That(total, Is.GreaterThan(1_000), "the queries must hit something, or identical empty answers pass trivially");
    }

    [Test]
    [VerifiesRule("SQ-03")]
    public void Count_AfterSomeMoveNextCalls_CountsTheRest_AndExhausts()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 500, origin: 0f, extent: 1_000f, seed: 7);
        var cs = ClusterStateOf(dbe);
        var all = Q.Box2D(0d, 0d, 1_000d, 1_000d);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = Open(dbe, cs, all);
        try
        {
            const int Taken = 123;
            for (int i = 0; i < Taken; i++)
            {
                Assert.That(e.MoveNext(), Is.True);
            }

            Assert.That(e.Count(), Is.EqualTo(500 - Taken), "Count() must continue from where MoveNext stopped");
            Assert.That(e.MoveNext(), Is.False, "Count() leaves the enumerator exhausted");
            Assert.That(e.Count(), Is.Zero);
        }
        finally
        {
            e.Dispose();
        }
    }

    [Test]
    [VerifiesRule("SQ-03")]
    public void Fill_AfterSomeMoveNextCalls_ContinuesTheSameSequence()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 500, origin: 0f, extent: 1_000f, seed: 8);
        var cs = ClusterStateOf(dbe);
        var all = Q.Box2D(0d, 0d, 1_000d, 1_000d);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var reference = new List<ClusterSpatialQueryResult>();
        var e = Open(dbe, cs, all);
        try
        {
            while (e.MoveNext())
            {
                reference.Add(e.Current);
            }
        }
        finally
        {
            e.Dispose();
        }

        var mixed = new List<ClusterSpatialQueryResult>();
        var buffer = new ClusterSpatialQueryResult[13];
        e = Open(dbe, cs, all);
        try
        {
            for (int i = 0; i < 77 && e.MoveNext(); i++)
            {
                mixed.Add(e.Current);
            }

            int n;
            while ((n = e.Fill(buffer)) > 0)
            {
                mixed.AddRange(buffer.AsSpan(0, n).ToArray());
                if (e.MoveNext())
                {
                    mixed.Add(e.Current);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        Assert.That(mixed, Is.EqualTo(reference), "MoveNext and Fill interleaved must yield the same sequence as MoveNext alone");
    }

    [Test]
    public void Fill_IntoAnEmptySpan_ConsumesNothing()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 200, origin: 0f, extent: 1_000f, seed: 9);
        var cs = ClusterStateOf(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = Open(dbe, cs, Q.Box2D(0d, 0d, 1_000d, 1_000d));
        try
        {
            Assert.That(e.Fill(Span<ClusterSpatialQueryResult>.Empty), Is.Zero);
            Assert.That(e.Count(), Is.EqualTo(200), "an empty Fill must not have consumed anything");
        }
        finally
        {
            e.Dispose();
        }
    }

    [Test]
    public void Drains_OverAnEmptyRegion_ReturnZero()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        var population = Spawn(dbe, 200, origin: 0f, extent: 300f, seed: 10);
        var cs = ClusterStateOf(dbe);

        Assert.That(AssertDrains(dbe, cs, population, Q.Box2D(700d, 700d, 900d, 900d), "empty box"), Is.Zero);
        Assert.That(AssertDrains(dbe, cs, population, Q.Sphere2D(800d, 800d, 50d), "empty sphere"), Is.Zero);
    }

    // ── The AABB2F block kernel (NarrowphaseAabb2F): an AABB2F-only component, the only layout it takes ──────────────────────────────────────────────

    private DatabaseEngine SetupBlockEngine(float cellSize, float worldMax, int promoteThreshold = 0)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClReachPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(worldMax, worldMax), cellSize));
        if (promoteThreshold > 0)
        {
            dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
            dbe.ClusterCellTreePromoteTightness = 1f;
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary><see cref="Spawn"/>'s population on <see cref="ClReachUnit"/>, whose component is nothing but its AABB2F.</summary>
    private static List<Spawned> SpawnBlock(DatabaseEngine dbe, int count, float extent, int seed)
    {
        var rng = new Random(seed);
        var population = new List<Spawned>(count);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                float x = 5f + ((float)rng.NextDouble() * (extent - 10f));
                float y = 5f + ((float)rng.NextDouble() * (extent - 10f));
                float half = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 5f;
                var b = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
                var id = tx.Spawn<ClReachUnit>(ClReachUnit.Pos.Set(new ClReachPos { Bounds = b }));
                population.Add(new Spawned((long)id.RawValue, b.MinX, b.MinY, 0d, b.MaxX, b.MaxY, 0d));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return population;
    }

    /// <summary>
    /// The oracle, with the kernel on and with it off: scattered clusters (a few entities per cell) and full ones (4 800 entities in one cell). Both paths
    /// must return the oracle's set with the same bounds and squared distances bit for bit, and Count and Fill must agree with MoveNext — so the kernel and
    /// the loop answer identically without either being compared to the other directly.
    /// </summary>
    [TestCase(100f, 1_000f, 3_000, 0, TestName = "Drains_MatchTheOracle_WithTheAabb2FBlockKernel_AndWithout(scattered)")]
    [TestCase(1_000f, 4_000f, 4_800, 0, TestName = "Drains_MatchTheOracle_WithTheAabb2FBlockKernel_AndWithout(fullClusters)")]
    [TestCase(1_000f, 4_000f, 3_000, 24, TestName = "Drains_MatchTheOracle_WithTheAabb2FBlockKernel_AndWithout(promotedCell)")]
    [VerifiesRule("SQ-03")]
    // It flips the process-wide switch, and a query another fixture builds meanwhile would silently take the scalar path.
    [NonParallelizable]
    public void Drains_MatchTheOracle_WithTheAabb2FBlockKernel_AndWithout(float cellSize, float worldMax, int count, int promoteThreshold)
    {
        Assume.That(NarrowphaseAabb2F.Best, Is.Not.EqualTo(NarrowphaseAabb2F.Kernel.None), "no block kernel on this machine");
        using var dbe = SetupBlockEngine(cellSize, worldMax, promoteThreshold);
        var population = SpawnBlock(dbe, count, 1_000f, seed: 40);
        var cs = dbe._archetypeStates[Archetype<ClReachUnit>.Metadata.ArchetypeId].ClusterState;
        if (promoteThreshold > 0)
        {
            Assert.That(cs.Realm0Spatial.PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote, or the tree path never runs");
        }
        var queries = RandomQueries2D(1_000d, seed: 41, count: 20).ToList();
        var saved = SpatialQueryTuning.SimdNarrowphase;
        try
        {
            foreach (var kernel in new[] { true, false })
            {
                SpatialQueryTuning.SimdNarrowphase = kernel;
                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    var probe = Open(dbe, cs, queries[0]);
                    try
                    {
                        Assert.That(probe.UsesAabb2FBlocks, Is.EqualTo(kernel), "precondition: the switch must decide the path");
                    }
                    finally
                    {
                        probe.Dispose();
                    }
                }

                var what = $"{(kernel ? "block kernel" : "scalar loop")}, {count} in {cellSize} m cells";
                var total = queries.Sum(q => AssertDrains(dbe, cs, population, q, what));
                Assert.That(total, Is.GreaterThan(1_000), $"{what}: the queries must hit something, or identical empty answers pass trivially");
            }
        }
        finally
        {
            SpatialQueryTuning.SimdNarrowphase = saved;
        }
    }

    /// <summary>
    /// A cluster size that is not a multiple of 16: the kernel takes the whole blocks and the loop the slots past the last one. The filler component makes
    /// the cluster 59 slots (<c>ArchetypeClusterInfo.SelectClusterSize</c>), three blocks and an 11-slot tail.
    /// </summary>
    [Test]
    [VerifiesRule("SQ-03")]
    public void Drains_MatchTheOracle_WhenTheClusterEndsInAPartialBlock()
    {
        Assume.That(NarrowphaseAabb2F.Best, Is.Not.EqualTo(NarrowphaseAabb2F.Kernel.None), "no block kernel on this machine");
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        using var disposeDbe = dbe;
        dbe.RegisterComponentFromAccessor<ClTailPos>();
        dbe.RegisterComponentFromAccessor<ClTailFiller>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(4_000f, 4_000f), 1_000f));
        dbe.InitializeArchetypes();

        var rng = new Random(43);
        var population = new List<Spawned>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 2_000; i++)
            {
                float x = 5f + ((float)rng.NextDouble() * 990f);
                float y = 5f + ((float)rng.NextDouble() * 990f);
                float half = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 5f;
                var b = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
                var id = tx.Spawn<ClTailUnit>(ClTailUnit.Pos.Set(new ClTailPos { Bounds = b }), ClTailUnit.Filler.Set(new ClTailFiller()));
                population.Add(new Spawned((long)id.RawValue, b.MinX, b.MinY, 0d, b.MaxX, b.MaxY, 0d));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = dbe._archetypeStates[Archetype<ClTailUnit>.Metadata.ArchetypeId].ClusterState;
        var clusterSize = cs.Layout.ClusterSize;
        Assert.That(clusterSize % NarrowphaseAabb2F.BlockSize != 0 && clusterSize > NarrowphaseAabb2F.BlockSize, Is.True,
            $"precondition: the cluster must end in a partial block, and hold at least one whole one; it holds {clusterSize}");

        var queries = RandomQueries2D(1_000d, seed: 44, count: 20).ToList();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var probe = Open(dbe, cs, queries[0]);
            try
            {
                Assert.That(probe.UsesAabb2FBlocks, Is.True, "precondition: the kernel must be the path under test");
            }
            finally
            {
                probe.Dispose();
            }
        }

        var total = queries.Sum(q => AssertDrains(dbe, cs, population, q, $"{clusterSize}-slot clusters"));
        Assert.That(total, Is.GreaterThan(1_000), "the queries must hit something, or identical empty answers pass trivially");
    }

    /// <summary>
    /// The two resume contracts through the kernel: Count after MoveNext counts the rest, and MoveNext / Fill interleaved yield MoveNext's sequence.
    /// </summary>
    [Test]
    [VerifiesRule("SQ-03")]
    public void Resume_AfterMoveNext_ThroughTheAabb2FBlockKernel()
    {
        Assume.That(NarrowphaseAabb2F.Best, Is.Not.EqualTo(NarrowphaseAabb2F.Kernel.None), "no block kernel on this machine");
        using var dbe = SetupBlockEngine(cellSize: 100f, worldMax: 1_000f);
        SpawnBlock(dbe, 500, 1_000f, seed: 42);
        var cs = dbe._archetypeStates[Archetype<ClReachUnit>.Metadata.ArchetypeId].ClusterState;
        var all = Q.Box2D(0d, 0d, 1_000d, 1_000d);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var reference = new List<ClusterSpatialQueryResult>();
        var e = Open(dbe, cs, all);
        try
        {
            Assert.That(e.UsesAabb2FBlocks, Is.True, "precondition: the kernel must be the path under test");
            while (e.MoveNext())
            {
                reference.Add(e.Current);
            }
        }
        finally
        {
            e.Dispose();
        }

        e = Open(dbe, cs, all);
        try
        {
            for (int i = 0; i < 123; i++)
            {
                Assert.That(e.MoveNext(), Is.True);
            }

            Assert.That(e.Count(), Is.EqualTo(500 - 123), "Count() must continue from where MoveNext stopped");
            Assert.That(e.MoveNext(), Is.False, "Count() leaves the enumerator exhausted");
        }
        finally
        {
            e.Dispose();
        }

        var mixed = new List<ClusterSpatialQueryResult>();
        var buffer = new ClusterSpatialQueryResult[13];
        e = Open(dbe, cs, all);
        try
        {
            for (int i = 0; i < 77 && e.MoveNext(); i++)
            {
                mixed.Add(e.Current);
            }

            int n;
            while ((n = e.Fill(buffer)) > 0)
            {
                mixed.AddRange(buffer.AsSpan(0, n).ToArray());
                if (e.MoveNext())
                {
                    mixed.Add(e.Current);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        Assert.That(mixed, Is.EqualTo(reference), "MoveNext and Fill interleaved must yield the same sequence as MoveNext alone");
    }

    // ── Lifetime of the rented window, through the public API ────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SQ-05")]
    public void UsingAnEnumeratorAfterItsCopyHandedTheWindowBack_Throws_AndASecondDisposeIsHarmless()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 500, origin: 0f, extent: 1_000f, seed: 11);
        var cs = ClusterStateOf(dbe);
        var all = Q.Box2D(0d, 0d, 1_000d, 1_000d);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = Open(dbe, cs, all);
        Assert.That(e.MoveNext(), Is.True, "precondition: the enumerator must have rented its window");

        // GetEnumerator() returns a copy carrying the same rent, as foreach would use it; disposing the copy hands the window back.
        var copy = e.GetEnumerator();
        copy.Dispose();

        bool threw = false;
        try
        {
            e.MoveNext();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.That(threw, Is.True, "the original kept draining a window it no longer holds");
        e.Dispose();
        e.Dispose();

        var again = Open(dbe, cs, all);
        try
        {
            Assert.That(again.Count(), Is.EqualTo(500), "a stale return must not have broken this thread's cache");
        }
        finally
        {
            again.Dispose();
        }
    }

    [Test]
    public void MoveNext_AfterDispose_FindsNothing()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 300, origin: 0f, extent: 1_000f, seed: 12);
        var cs = ClusterStateOf(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = Open(dbe, cs, Q.Box2D(0d, 0d, 1_000d, 1_000d));
        Assert.That(e.MoveNext(), Is.True, "precondition: a cluster must be open mid-drain");
        e.Dispose();
        var moved = e.MoveNext();
        var counted = e.Count();
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.False, "a disposed enumerator drained a cluster its window no longer pins");
            Assert.That(counted, Is.Zero);
        });
    }

    [Test]
    public void CountOnTheQueryItself_HandsItsWindowBack()
    {
        using var dbe = SetupEngine(cellSize: 100f, worldMax: 1_000f);
        Spawn(dbe, 300, origin: 0f, extent: 1_000f, seed: 13);
        var segment = ClusterStateOf(dbe).ClusterSegment;
        var box = new AABB2F { MinX = 0f, MinY = 0f, MaxX = 1_000f, MaxY = 1_000f };

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var cache = SpatialQueryAccessorCache.Instance;
        var window = cache.Rent(segment, out var token);
        SpatialQueryAccessorCache.Return(window, token);

        for (int i = 0; i < 50; i++)
        {
            // Drained on an rvalue: no local, so nothing can ever call Dispose.
            Assert.That(dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in box).Count(), Is.EqualTo(300));
        }

        var after = cache.Rent(segment, out var t2);
        SpatialQueryAccessorCache.Return(after, t2);
        Assert.That(after, Is.SameAs(window), "a drained query kept its window rented, so the next rent had to take another");
    }

    // ── Every storage tier, and every bounds reader against the reference decode ──────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    [VerifiesRule("SQ-06")]
    public void Drains_MatchTheOracle_OnEveryStorageTier()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        using var disposeDbe = dbe;
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.RegisterComponentFromAccessor<F64Pos2>();
        dbe.RegisterComponentFromAccessor<F64Pos3>();
        dbe.RegisterComponentFromAccessor<F64Ball>();
        // Coarse cells keep five archetypes' clusters few: the test cache is 8 MiB (1 024 pages).
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector3(-1_000f, -1_000f, -1_000f), new Vector3(1_000f, 1_000f, 1_000f), 250f));
        dbe.InitializeArchetypes();

        const int PerTier = 500;
        var p2f = new List<Spawned>();
        var p3f = new List<Spawned>();
        var p2d = new List<Spawned>();
        var p3d = new List<Spawned>();
        var pBall = new List<Spawned>();
        var rng = new Random(20);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < PerTier; i++)
            {
                float x = -900f + ((float)rng.NextDouble() * 1_800f);
                float y = -900f + ((float)rng.NextDouble() * 1_800f);
                float z = -900f + ((float)rng.NextDouble() * 1_800f);
                float h = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 5f;
                var b2f = new AABB2F { MinX = x - h, MinY = y - h, MaxX = x + h, MaxY = y + h };
                var b3f = new AABB3F { MinX = x - h, MinY = y - h, MinZ = z - h, MaxX = x + h, MaxY = y + h, MaxZ = z + h };
                var b2d = new AABB2D { MinX = x - h, MinY = y - h, MaxX = x + h, MaxY = y + h };
                var b3d = new AABB3D { MinX = x - h, MinY = y - h, MinZ = z - h, MaxX = x + h, MaxY = y + h, MaxZ = z + h };
                var ball = new BSphere3D { CenterX = x, CenterY = y, CenterZ = z, Radius = h + 0.5 };
                var pos2f = new ClCohPos { Bounds = b2f, Mass = 1f };
                var pos3f = new ClSpatialPos { Bounds = b3f };
                var meta = new ClSpatialMeta();
                var pos2d = new F64Pos2 { Bounds = b2d };
                var pos3d = new F64Pos3 { Bounds = b3d };
                var posBall = new F64Ball { Ball = ball };
                p2f.Add(new Spawned((long)tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos2f)).RawValue, b2f.MinX, b2f.MinY, 0d, b2f.MaxX, b2f.MaxY, 0d));
                p3f.Add(new Spawned((long)tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos3f), ClSpatialUnit.Meta.Set(in meta)).RawValue,
                    b3f.MinX, b3f.MinY, b3f.MinZ, b3f.MaxX, b3f.MaxY, b3f.MaxZ));
                p2d.Add(new Spawned((long)tx.Spawn<F64Unit2>(F64Unit2.Pos.Set(in pos2d)).RawValue, b2d.MinX, b2d.MinY, 0d, b2d.MaxX, b2d.MaxY, 0d));
                p3d.Add(new Spawned((long)tx.Spawn<F64Unit3>(F64Unit3.Pos.Set(in pos3d)).RawValue, b3d.MinX, b3d.MinY, b3d.MinZ, b3d.MaxX, b3d.MaxY, b3d.MaxZ));
                var enclosing = SpatialGeometry.Enclosing(ball);
                pBall.Add(new Spawned((long)tx.Spawn<F64BallUnit>(F64BallUnit.Ball.Set(in posBall)).RawValue,
                    enclosing.MinX, enclosing.MinY, enclosing.MinZ, enclosing.MaxX, enclosing.MaxY, enclosing.MaxZ));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        (string name, ArchetypeClusterState cs, List<Spawned> population, bool is3D)[] tiers =
        [
            ("AABB2F", dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState, p2f, false),
            ("AABB3F", dbe._archetypeStates[Archetype<ClSpatialUnit>.Metadata.ArchetypeId].ClusterState, p3f, true),
            ("AABB2D", dbe._archetypeStates[Archetype<F64Unit2>.Metadata.ArchetypeId].ClusterState, p2d, false),
            ("AABB3D", dbe._archetypeStates[Archetype<F64Unit3>.Metadata.ArchetypeId].ClusterState, p3d, true),
            ("BSphere3D", dbe._archetypeStates[Archetype<F64BallUnit>.Metadata.ArchetypeId].ClusterState, pBall, true),
        ];

        foreach (var (name, cs, population, is3D) in tiers)
        {
            var qrng = new Random(21);
            int total = 0;
            for (int i = 0; i < 12; i++)
            {
                double x = -900d + (qrng.NextDouble() * 1_500d);
                double y = -900d + (qrng.NextDouble() * 1_500d);
                double z = -900d + (qrng.NextDouble() * 1_500d);
                double s = 100d + (qrng.NextDouble() * 400d);
                total += AssertDrains(dbe, cs, population, new Q(is3D, false, x, y, z, x + s, y + s, z + s, 0d), name);
                total += AssertDrains(dbe, cs, population, new Q(is3D, true, x, y, z, 0d, 0d, 0d, s), name);
            }

            Assert.That(total, Is.GreaterThan(0), $"{name}: the queries must hit something, or identical empty answers pass trivially");
        }
    }

    /// <summary>
    /// Each tier's bounds reader against <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/>, the decode the rest of the engine (ray, kNN, frustum,
    /// the cluster-bound recompute) still uses: the same validity and the same doubles, on valid, NaN, inverted, infinite and zero-extent inputs. If the two
    /// ever disagreed, this query would answer a different question from the cluster bound its broadphase trusts.
    /// </summary>
    [Test]
    public void EveryBoundsReader_AgreesWithTheReferenceDecode()
    {
        float[] floats = [0f, -3.5f, 7.25f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, 1e30f];
        double[] doubles = [0d, -3.5, 7.25, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0d, 1e300];
        var rng = new Random(30);
        int checkedCases = 0;
        for (int i = 0; i < 400; i++)
        {
            float F() => floats[rng.Next(floats.Length)];
            double D() => doubles[rng.Next(doubles.Length)];

            var a2f = new AABB2F { MinX = F(), MinY = F(), MaxX = F(), MaxY = F() };
            var a3f = new AABB3F { MinX = F(), MinY = F(), MinZ = F(), MaxX = F(), MaxY = F(), MaxZ = F() };
            var s2f = new BSphere2F { CenterX = F(), CenterY = F(), Radius = F() };
            var s3f = new BSphere3F { CenterX = F(), CenterY = F(), CenterZ = F(), Radius = F() };
            var a2d = new AABB2D { MinX = D(), MinY = D(), MaxX = D(), MaxY = D() };
            var a3d = new AABB3D { MinX = D(), MinY = D(), MinZ = D(), MaxX = D(), MaxY = D(), MaxZ = D() };
            var s2d = new BSphere2D { CenterX = D(), CenterY = D(), Radius = D() };
            var s3d = new BSphere3D { CenterX = D(), CenterY = D(), CenterZ = D(), Radius = D() };

            checkedCases += Check<AabbClusterEnumerator.Aabb2FReader>((byte*)&a2f, SpatialFieldType.AABB2F, sizeof(AABB2F));
            checkedCases += Check<AabbClusterEnumerator.Aabb3FReader>((byte*)&a3f, SpatialFieldType.AABB3F, sizeof(AABB3F));
            checkedCases += Check<AabbClusterEnumerator.BSphere2FReader>((byte*)&s2f, SpatialFieldType.BSphere2F, sizeof(BSphere2F));
            checkedCases += Check<AabbClusterEnumerator.BSphere3FReader>((byte*)&s3f, SpatialFieldType.BSphere3F, sizeof(BSphere3F));
            checkedCases += Check<AabbClusterEnumerator.Aabb2DReader>((byte*)&a2d, SpatialFieldType.AABB2D, sizeof(AABB2D));
            checkedCases += Check<AabbClusterEnumerator.Aabb3DReader>((byte*)&a3d, SpatialFieldType.AABB3D, sizeof(AABB3D));
            checkedCases += Check<AabbClusterEnumerator.BSphere2DReader>((byte*)&s2d, SpatialFieldType.BSphere2D, sizeof(BSphere2D));
            checkedCases += Check<AabbClusterEnumerator.BSphere3DReader>((byte*)&s3d, SpatialFieldType.BSphere3D, sizeof(BSphere3D));
        }

        Assert.That(checkedCases, Is.GreaterThan(400), "precondition: some inputs must be valid, or only the validity bit was compared");
    }

    /// <summary>Compare one reader with the reference on one field; returns 1 when the input was valid (so the bounds were compared too).</summary>
    private static int Check<TReader>(byte* field, SpatialFieldType type, int size) where TReader : struct, AabbClusterEnumerator.IBoundsReader
    {
        Span<double> reference = stackalloc double[6];
        var fi = new SpatialFieldInfo(0, size, type, 1f);
        bool referenceValid = SpatialMaintainer.ReadAndValidateBoundsFromPtr(field, fi, reference);
        bool readerValid = TReader.Read(field, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        Assert.That(readerValid, Is.EqualTo(referenceValid), $"{type}: the reader and the reference disagree on validity");
        if (!readerValid)
        {
            return 0;
        }

        bool same = TReader.Is3D
            ? SameBits(minX, reference[0]) && SameBits(minY, reference[1]) && SameBits(minZ, reference[2])
              && SameBits(maxX, reference[3]) && SameBits(maxY, reference[4]) && SameBits(maxZ, reference[5])
            : SameBits(minX, reference[0]) && SameBits(minY, reference[1]) && SameBits(maxX, reference[2]) && SameBits(maxY, reference[3]);
        Assert.That(same, Is.True, $"{type}: the reader decoded different bounds from the reference");
        Assert.That(TReader.Is3D, Is.EqualTo(type.Is3D()), $"{type}: the reader's dimensionality is not the field type's");
        return 1;
    }
}
