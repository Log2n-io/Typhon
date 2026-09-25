using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Own archetypes: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).

/// <summary>The kernel's layout — an AABB2F and nothing else — in category 8, so a mask can reject it.</summary>
[Component("Typhon.Test.ClBatch.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClBatchPos
{
    [Field]
    [SpatialIndex(Category = ClusterRadiusBatchTests.Category)]
    public AABB2F Bounds;
}

[Archetype]
partial class ClBatchUnit : Archetype<ClBatchUnit>
{
    public static readonly Comp<ClBatchPos> Pos = Register<ClBatchPos>();
}

/// <summary>An AABB2F with company: the component is not the kernel's 16-byte stride, so every cluster goes through the loop.</summary>
[Component("Typhon.Test.ClBatch.Wide", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClBatchWide
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field] public int A;
    [Field] public int B;
    [Field] public int C;
}

[Archetype]
partial class ClBatchWideUnit : Archetype<ClBatchWideUnit>
{
    public static readonly Comp<ClBatchWide> Pos = Register<ClBatchWide>();
}

/// <summary>The other 2D f32 storage tier, read through its sphere's enclosing box.</summary>
[Component("Typhon.Test.ClBatch.Sphere", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClBatchSphere
{
    [Field]
    [SpatialIndex]
    public BSphere2F Bounds;
}

[Archetype]
partial class ClBatchSphereUnit : Archetype<ClBatchSphereUnit>
{
    public static readonly Comp<ClBatchSphere> Pos = Register<ClBatchSphere>();
}

/// <summary>A 2D f64 archetype: a batch takes BSphere2F members, so it must refuse this tier.</summary>
[Component("Typhon.Test.ClBatch.F64", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClBatchF64
{
    [Field]
    [SpatialIndex]
    public AABB2D Bounds;
}

[Archetype]
partial class ClBatchF64Unit : Archetype<ClBatchF64Unit>
{
    public static readonly Comp<ClBatchF64> Pos = Register<ClBatchF64>();
}

/// <summary>
/// <see cref="ClusterSpatialQuery{TArch}.CountRadius"/> and <see cref="ClusterSpatialQuery{TArch}.ForEachInRadius{TSink}"/>: each member of a batch must be
/// answered exactly as its own <see cref="ClusterSpatialQuery{TArch}.Radius(in BSphere2F, uint)"/> query answers it — the same count, and the same hits
/// in the same order, bit for bit — on every broadphase path (scalar scan, batched scan, a promoted cell's tree), with the block kernel on and off, on
/// each 2D f32 layout, under category masks that admit and reject, for the named outliers, and for members that retire.
/// </summary>
[TestFixture]
// The kernel cases flip the process-wide narrowphase switch, and a query another fixture builds meanwhile would silently take the other path.
[NonParallelizable]
class ClusterRadiusBatchTests : TestBase<ClusterRadiusBatchTests>
{
    /// <summary><see cref="ClBatchUnit"/>'s category: mask <see cref="Category"/> admits it, <see cref="OtherCategory"/> rejects it.</summary>
    internal const uint Category = 8u;

    private const uint OtherCategory = 1u;

    private DatabaseEngine Setup(float cellSize, float worldMax, int promoteThreshold = 0)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClBatchPos>();
        dbe.RegisterComponentFromAccessor<ClBatchWide>();
        dbe.RegisterComponentFromAccessor<ClBatchSphere>();
        dbe.RegisterComponentFromAccessor<ClBatchF64>();
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

    private static ArchetypeClusterState StateOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>, new() =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;

    /// <summary>The same population in the three f32 archetypes, inside [5, extent - 5]²: a third points, the rest boxes (or spheres) up to 5 across.</summary>
    private static void Spawn(DatabaseEngine dbe, int count, float extent, int seed)
    {
        var rng = new Random(seed);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                float x = 5f + ((float)rng.NextDouble() * (extent - 10f));
                float y = 5f + ((float)rng.NextDouble() * (extent - 10f));
                float half = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 5f;
                var b = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
                tx.Spawn<ClBatchUnit>(ClBatchUnit.Pos.Set(new ClBatchPos { Bounds = b }));
                tx.Spawn<ClBatchWideUnit>(ClBatchWideUnit.Pos.Set(new ClBatchWide { Bounds = b, A = i }));
                var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = half };
                tx.Spawn<ClBatchSphereUnit>(ClBatchSphereUnit.Pos.Set(new ClBatchSphere { Bounds = sphere }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>
    /// Batches shaped like a source cluster's members: sizes 1, 2, 17 and 64 around a random centre, each with its own radius; one with a member diagonally
    /// far from the others (a teleported entity), one with two members at opposite ends of one row (the walk jumps the cells between them), one with a
    /// negative radius among ordinary members, and one with every member at the same point.
    /// </summary>
    private static IEnumerable<BSphere2F[]> Batches(float extent, int seed)
    {
        var rng = new Random(seed);
        BSphere2F Member(float cx, float cy, float spread) => new()
        {
            CenterX = Math.Clamp(cx + (((float)rng.NextDouble() - 0.5f) * spread), 0f, extent),
            CenterY = Math.Clamp(cy + (((float)rng.NextDouble() - 0.5f) * spread), 0f, extent),
            Radius = 5f + ((float)rng.NextDouble() * extent * 0.12f),
        };

        foreach (var size in new[] { 1, 2, 17, 64, 17, 64 })
        {
            float cx = (float)rng.NextDouble() * extent, cy = (float)rng.NextDouble() * extent;
            var batch = new BSphere2F[size];
            for (int j = 0; j < size; j++)
            {
                batch[j] = Member(cx, cy, extent * 0.1f);
            }

            yield return batch;
        }

        var far = new BSphere2F[20];
        for (int j = 0; j < far.Length; j++)
        {
            far[j] = j == 7 ? Member(extent * 0.95f, extent * 0.95f, 1f) : Member(extent * 0.1f, extent * 0.1f, extent * 0.1f);
        }

        yield return far;

        yield return [Member(extent * 0.05f, extent * 0.5f, 1f), Member(extent * 0.95f, extent * 0.5f, 1f), Member(extent * 0.06f, extent * 0.5f, 1f)];

        var inverted = new BSphere2F[5];
        for (int j = 0; j < inverted.Length; j++)
        {
            inverted[j] = Member(extent * 0.5f, extent * 0.3f, extent * 0.05f);
        }

        inverted[2].Radius = -inverted[2].Radius;
        yield return inverted;

        var same = new BSphere2F[9];
        Array.Fill(same, Member(extent * 0.5f, extent * 0.5f, 0f));
        yield return same;
    }

    /// <summary>Records each member's hits; a member retires once it holds <see cref="Limit"/>[member] of them, when a limit is set.</summary>
    private struct ListSink : IRadiusBatchSink
    {
        public List<ClusterSpatialQueryResult>[] Hits;
        public int[] Limit;

        public ListSink(int members, int[] limit = null)
        {
            Hits = new List<ClusterSpatialQueryResult>[members];
            for (int j = 0; j < members; j++)
            {
                Hits[j] = [];
            }

            Limit = limit;
        }

        public bool Hit(int member, in ClusterSpatialQueryResult hit)
        {
            Hits[member].Add(hit);
            return Limit == null || Hits[member].Count < Limit[member];
        }
    }

    /// <summary>
    /// A member's own query through MoveNext, stopped after <paramref name="limit"/> hits as a caller breaking out of its loop would stop it.
    /// </summary>
    private static List<ClusterSpatialQueryResult> Single<TArch>(DatabaseEngine dbe, in BSphere2F member, uint categoryMask, int limit = int.MaxValue)
        where TArch : Archetype<TArch>, new()
    {
        var hits = new List<ClusterSpatialQueryResult>();
        var e = dbe.ClusterSpatialQuery<TArch>().Radius(in member, categoryMask);
        try
        {
            while (hits.Count < limit && e.MoveNext())
            {
                hits.Add(e.Current);
            }
        }
        finally
        {
            e.Dispose();
        }

        return hits;
    }

    /// <summary>The archetype's query tally so far (SO-02): clusters opened, entities tested, matches.</summary>
    private static (long Clusters, long Candidates, long Hits) TallyOf(ArchetypeClusterState cs)
    {
        var tally = cs.QueryTally;
        if (tally == null)
        {
            return (0, 0, 0);
        }

        tally.Read(out var clusters, out var candidates, out var hits);
        return (clusters, candidates, hits);
    }

    private static (long, long, long) Since((long Clusters, long Candidates, long Hits) before, (long Clusters, long Candidates, long Hits) after) =>
        (after.Clusters - before.Clusters, after.Candidates - before.Candidates, after.Hits - before.Hits);

    /// <summary>Bit for bit, in order: <c>Equals</c> on a struct of doubles would take -0 for 0.</summary>
    private static void AssertSameHits(List<ClusterSpatialQueryResult> actual, List<ClusterSpatialQueryResult> expected, string what)
    {
        if (!MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(actual)).SequenceEqual(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(expected))))
        {
            Assert.That(actual, Is.EqualTo(expected), what);
            Assert.Fail($"{what}: the same hits, but not the same bits");
        }
    }

    /// <summary>
    /// Both batch forms against each member's own query, then the sink form again with members retiring after 1–4 hits; returns the hits in total so a
    /// test can tell it asked something. Each batch must also tally what its members' own queries tally (SO-02): the same clusters, entities and
    /// matches, the retiring batch against queries stopped at the same hit.
    /// </summary>
    private static int AssertBatchAnswersAsSingles<TArch>(DatabaseEngine dbe, BSphere2F[] members, string what, uint categoryMask = uint.MaxValue)
        where TArch : Archetype<TArch>, new()
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var cs = StateOf<TArch>(dbe);
        var tally = TallyOf(cs);
        var expected = new List<ClusterSpatialQueryResult>[members.Length];
        var total = 0;
        for (int j = 0; j < members.Length; j++)
        {
            expected[j] = Single<TArch>(dbe, in members[j], categoryMask);
            total += expected[j].Count;
        }

        var singlesTally = Since(tally, tally = TallyOf(cs));

        var counts = new int[members.Length];
        Array.Fill(counts, -1);
        dbe.ClusterSpatialQuery<TArch>().CountRadius(members, counts, categoryMask);
        var countTally = Since(tally, tally = TallyOf(cs));
        var sink = new ListSink(members.Length);
        dbe.ClusterSpatialQuery<TArch>().ForEachInRadius(members, ref sink, categoryMask);
        var sinkTally = Since(tally, tally = TallyOf(cs));

        var limit = new int[members.Length];
        for (int j = 0; j < members.Length; j++)
        {
            limit[j] = 1 + (j % 4);
            Single<TArch>(dbe, in members[j], categoryMask, limit[j]);
        }

        var stoppedTally = Since(tally, tally = TallyOf(cs));
        var retiring = new ListSink(members.Length, limit);
        dbe.ClusterSpatialQuery<TArch>().ForEachInRadius(members, ref retiring, categoryMask);
        var retiringTally = Since(tally, TallyOf(cs));

        for (int j = 0; j < members.Length; j++)
        {
            var who = $"{what}, member {j} of {members.Length} ({Show(members[j])})";
            Assert.That(counts[j], Is.EqualTo(expected[j].Count), $"{who}: CountRadius");
            AssertSameHits(sink.Hits[j], expected[j], $"{who}: ForEachInRadius");
            AssertSameHits(retiring.Hits[j], expected[j].GetRange(0, Math.Min(limit[j], expected[j].Count)), $"{who}: retiring after {limit[j]}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(countTally, Is.EqualTo(singlesTally), $"{what}: CountRadius's tally (clusters, candidates, hits) against its members' own queries'");
            Assert.That(sinkTally, Is.EqualTo(singlesTally), $"{what}: ForEachInRadius's tally against its members' own queries'");
            Assert.That(retiringTally, Is.EqualTo(stoppedTally),
                $"{what}: the retiring batch's tally against its members' queries stopped at the same hit");
        });

        return total;
    }

    /// <summary>A mask that admits <see cref="ClBatchUnit"/> answers as the default mask does, and one that rejects it answers nothing, batch and single.</summary>
    private static void AssertCategoryMasks(DatabaseEngine dbe, BSphere2F[] members, int unfiltered, string what)
    {
        Assert.That(AssertBatchAnswersAsSingles<ClBatchUnit>(dbe, members, $"{what}, admitting mask", Category), Is.EqualTo(unfiltered),
            $"{what}: a mask holding the archetype's category admits every cluster");
        Assert.That(AssertBatchAnswersAsSingles<ClBatchUnit>(dbe, members, $"{what}, rejecting mask", OtherCategory), Is.Zero,
            $"{what}: a mask missing the archetype's category rejects every cluster");
        AssertBatchAnswersAsSingles<ClBatchUnit>(dbe, members, $"{what}, no mask", 0u);
    }

    private static string Show(BSphere2F s) => $"({s.CenterX}, {s.CenterY}) r {s.Radius}";

    // ── Each member answered as its own query ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [TestCase(100f, 1_000f, 2_000, 0, true, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(scattered,kernel)")]
    [TestCase(100f, 1_000f, 2_000, 0, false, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(scattered,loop)")]
    [TestCase(1_000f, 4_000f, 4_800, 0, true, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(fullCells,kernel)")]
    [TestCase(1_000f, 4_000f, 4_800, 0, false, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(fullCells,loop)")]
    [TestCase(1_000f, 4_000f, 3_000, 24, true, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(promotedCell,kernel)")]
    [TestCase(1_000f, 4_000f, 3_000, 24, false, TestName = "EachMember_IsAnsweredAsItsOwnRadiusQuery(promotedCell,loop)")]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    [VerifiesRule("SO-02")]
    public void EachMember_IsAnsweredAsItsOwnRadiusQuery(float cellSize, float worldMax, int count, int promoteThreshold, bool kernel)
    {
        if (kernel)
        {
            Assume.That(NarrowphaseAabb2F.Best, Is.Not.EqualTo(NarrowphaseAabb2F.Kernel.None), "no block kernel on this machine");
        }

        var before = SpatialQueryTuning.SimdNarrowphase;
        SpatialQueryTuning.SimdNarrowphase = kernel;
        try
        {
            using var dbe = Setup(cellSize, worldMax, promoteThreshold);
            float extent = cellSize >= 1_000f ? 1_000f : worldMax;
            Spawn(dbe, count, extent, seed: 11);
            if (promoteThreshold > 0)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(StateOf<ClBatchUnit>(dbe).PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote");
                    Assert.That(StateOf<ClBatchWideUnit>(dbe).PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote");
                    Assert.That(StateOf<ClBatchSphereUnit>(dbe).PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must promote");
                });
            }

            var total = 0;
            foreach (var batch in Batches(extent, seed: 12))
            {
                var unfiltered = AssertBatchAnswersAsSingles<ClBatchUnit>(dbe, batch, "AABB2F");
                total += unfiltered;
                total += AssertBatchAnswersAsSingles<ClBatchWideUnit>(dbe, batch, "AABB2F, wide component");
                total += AssertBatchAnswersAsSingles<ClBatchSphereUnit>(dbe, batch, "BSphere2F");
                AssertCategoryMasks(dbe, batch, unfiltered, "AABB2F");
            }

            Assert.That(total, Is.GreaterThan(1_000), "the batches must hit something, or identical empty answers pass trivially");
        }
        finally
        {
            SpatialQueryTuning.SimdNarrowphase = before;
        }
    }

    /// <summary>
    /// A cluster whose box reaches further than the reach is named (EscapedClusters) and found by name. A member whose own range does not hold its home
    /// cell must find it after the walk — including members in its home cell's row or column, which a batch filtering cells on one axis only would visit
    /// the home cell for — and one whose range does must find it once, through the walk.
    /// </summary>
    [Test]
    [VerifiesRule("SQ-01")]
    [VerifiesRule("SQ-03")]
    [VerifiesRule("SO-02")]
    public void ANamedOutlier_IsFoundByEachMemberAsByItsOwnQuery()
    {
        using var dbe = Setup(100f, 1_000f);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    var p = new AABB2F { MinX = (x * 100f) + 50f, MinY = (y * 100f) + 50f, MaxX = (x * 100f) + 50f, MaxY = (y * 100f) + 50f };
                    tx.Spawn<ClBatchUnit>(ClBatchUnit.Pos.Set(new ClBatchPos { Bounds = p }));
                }
            }

            // Centre (250, 250), cell (2, 2); a 180 half-extent reaches 130 past that cell on every side.
            tx.Spawn<ClBatchUnit>(ClBatchUnit.Pos.Set(new ClBatchPos { Bounds = new AABB2F { MinX = 70f, MinY = 70f, MaxX = 430f, MaxY = 430f } }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(Volatile.Read(ref StateOf<ClBatchUnit>(dbe).DefaultRealmSpatial.EscapedClusters).Count, Is.EqualTo(1), "precondition: the outlier is named");

        BSphere2F[] members =
        [
            new() { CenterX = 425f, CenterY = 425f, Radius = 10f },   // cell (4, 4) only: the name finds it
            new() { CenterX = 415f, CenterY = 440f, Radius = 40f },   // cell (4, 4): the cell's own point through the walk, the outlier by name
            new() { CenterX = 250f, CenterY = 250f, Radius = 5f },    // its home cell: the walk finds it, once
            new() { CenterX = 350f, CenterY = 250f, Radius = 5f },    // the home cell's row, another column: by name
            new() { CenterX = 250f, CenterY = 350f, Radius = 5f },    // the home cell's column, another row: by name
            new() { CenterX = 900f, CenterY = 900f, Radius = 20f },   // nowhere near
        ];
        var total = AssertBatchAnswersAsSingles<ClBatchUnit>(dbe, members, "named outlier");
        Assert.That(total, Is.GreaterThanOrEqualTo(6), "the members must find the outlier, or identical empty answers pass trivially");
        AssertCategoryMasks(dbe, members, total, "named outlier");
    }

    // ── The sink and the window ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A sink that queries while the batch holds its window gets a window of its own (SQ-05), and both answers stay right.</summary>
    [Test]
    [VerifiesRule("SQ-05")]
    public void ASinkThatRunsItsOwnQuery_GetsItsOwnWindow()
    {
        using var dbe = Setup(100f, 1_000f);
        Spawn(dbe, 2_000, 1_000f, seed: 17);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var batch = new BSphere2F[12];
        for (int j = 0; j < batch.Length; j++)
        {
            batch[j] = new BSphere2F { CenterX = 300f + (j * 7f), CenterY = 600f - (j * 5f), Radius = 60f };
        }

        // This thread's window over the segment, left free: the batch rents exactly this entry, and holds it while the sink runs.
        var segment = StateOf<ClBatchUnit>(dbe).ClusterSegment;
        var window = SpatialQueryAccessorCache.Instance.Rent(segment, out var token);
        SpatialQueryAccessorCache.Return(window, token);

        var sink = new NestingSink { Dbe = dbe, Segment = segment, BatchWindow = window, Inner = [], Hits = new ListSink(batch.Length) };
        dbe.ClusterSpatialQuery<ClBatchUnit>().ForEachInRadius(batch, ref sink);
        Assert.That(sink.Inner, Is.Not.Empty, "precondition: the batch must hit something");
        Assert.That(sink.SharedWindow, Is.Zero, "a rent made inside the sink must never get the window the batch holds");
        for (int j = 0; j < batch.Length; j++)
        {
            AssertSameHits(sink.Hits.Hits[j], Single<ClBatchUnit>(dbe, in batch[j], uint.MaxValue), $"member {j}");
        }

        foreach (var (got, expected) in sink.Inner)
        {
            Assert.That(got, Is.EqualTo(expected), "the nested query");
        }
    }

    private struct NestingSink : IRadiusBatchSink
    {
        public DatabaseEngine Dbe;
        public ChunkBasedSegment<PersistentStore> Segment;
        public SpatialQueryAccessorCache.Entry BatchWindow;
        public List<(int Got, int Expected)> Inner;
        public ListSink Hits;
        public int SharedWindow;

        public bool Hit(int member, in ClusterSpatialQueryResult hit)
        {
            var mine = SpatialQueryAccessorCache.Instance.Rent(Segment, out var token);
            if (ReferenceEquals(mine, BatchWindow))
            {
                SharedWindow++;
            }

            SpatialQueryAccessorCache.Return(mine, token);

            var around = new BSphere2F { CenterX = (float)hit.MinX, CenterY = (float)hit.MinY, Radius = 25f };
            var e = Dbe.ClusterSpatialQuery<ClBatchUnit>().Radius(in around);
            int got;
            try
            {
                got = e.Count();
            }
            finally
            {
                e.Dispose();
            }

            Inner.Add((got, Single<ClBatchUnit>(Dbe, in around, uint.MaxValue).Count));
            return Hits.Hit(member, in hit);
        }
    }

    [Test]
    [VerifiesRule("SQ-05")]
    public void ASinkThatThrows_HandsTheWindowBack()
    {
        using var dbe = Setup(100f, 1_000f);
        Spawn(dbe, 500, 1_000f, seed: 18);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var batch = new[] { new BSphere2F { CenterX = 500f, CenterY = 500f, Radius = 200f } };
        var segment = StateOf<ClBatchUnit>(dbe).ClusterSegment;

        // This thread's window over the segment, left free: the batch below rents exactly this entry.
        var cache = SpatialQueryAccessorCache.Instance;
        var window = cache.Rent(segment, out var token);
        SpatialQueryAccessorCache.Return(window, token);

        // The member's own query, whose caller throws on its first hit: what the batch must tally when its sink does the same (SO-02).
        var cs = StateOf<ClBatchUnit>(dbe);
        var tally = TallyOf(cs);
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in dbe.ClusterSpatialQuery<ClBatchUnit>().Radius(in batch[0]))
            {
                throw new InvalidOperationException("the caller fails on purpose");
            }
        });
        var singleTally = Since(tally, tally = TallyOf(cs));

        var sink = new ThrowingSink();
        Assert.Throws<InvalidOperationException>(() => dbe.ClusterSpatialQuery<ClBatchUnit>().ForEachInRadius(batch, ref sink));
        Assert.That(sink.Calls, Is.EqualTo(1), "the sink's state reaches the caller even when it throws");
        Assert.Multiple(() =>
        {
            Assert.That(singleTally.Item3, Is.EqualTo(1), "precondition: the single query took one hit before its caller threw");
            Assert.That(Since(tally, TallyOf(cs)), Is.EqualTo(singleTally), "a batch whose sink throws tallies what its member's own query tallies");
        });

        // Still rented by the failed batch, the window would not be handed to the next rent.
        var again = cache.Rent(segment, out var againToken);
        SpatialQueryAccessorCache.Return(again, againToken);
        Assert.That(again, Is.SameAs(window), "the throwing batch kept its window");

        var counts = new int[1];
        dbe.ClusterSpatialQuery<ClBatchUnit>().CountRadius(batch, counts);
        Assert.That(counts[0], Is.GreaterThan(0));
    }

    private struct ThrowingSink : IRadiusBatchSink
    {
        public int Calls;

        public bool Hit(int member, in ClusterSpatialQueryResult hit)
        {
            Calls++;
            throw new InvalidOperationException("the sink fails on purpose");
        }
    }

    // ── Arguments ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Arguments_AreChecked_BeforeAnythingIsWritten()
    {
        using var dbe = Setup(100f, 1_000f);
        Spawn(dbe, 100, 1_000f, seed: 19);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var tooMany = new BSphere2F[65];
        var some = new[] { new BSphere2F { CenterX = 500f, CenterY = 500f, Radius = 900f }, new BSphere2F { CenterX = 10f, CenterY = 10f, Radius = 1f } };
        var notFinite = new[] { some[0], new BSphere2F { CenterX = float.NaN, CenterY = 10f, Radius = 1f } };
        var untouched = new[] { 42, 43 };

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => dbe.ClusterSpatialQuery<ClBatchUnit>().CountRadius(tooMany, new int[65]), "more than 64 members");
            Assert.Throws<ArgumentException>(() =>
            {
                var sink = new ListSink(65);
                dbe.ClusterSpatialQuery<ClBatchUnit>().ForEachInRadius(tooMany, ref sink);
            }, "more than 64 members, sink form");
            Assert.Throws<ArgumentException>(() => dbe.ClusterSpatialQuery<ClBatchUnit>().CountRadius(some, new int[1]), "counts shorter than members");
            Assert.Throws<ArgumentException>(() => dbe.ClusterSpatialQuery<ClBatchUnit>().CountRadius(notFinite, untouched), "a NaN member");
            Assert.That(untouched, Is.EqualTo(new[] { 42, 43 }), "a rejected batch leaves counts as they were");
            Assert.Throws<InvalidOperationException>(() => dbe.ClusterSpatialQuery<ClBatchF64Unit>().CountRadius(some, new int[2]), "a 2D f64 archetype");
            Assert.DoesNotThrow(() => dbe.ClusterSpatialQuery<ClBatchUnit>().CountRadius(ReadOnlySpan<BSphere2F>.Empty, untouched), "no members");
            Assert.That(untouched, Is.EqualTo(new[] { 42, 43 }), "no members: counts untouched");
        });
    }
}
