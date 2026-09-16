using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #960 — how the cluster chunk-id space behaves under aged churn, and therefore whether a block table can bound the
/// per-cluster side tables (<c>ClusterAabbs</c> and its siblings) by what is LIVE rather than by the id space.
/// </summary>
/// <remarks>
/// <para><b>What decides the design.</b> Every per-cluster side table is indexed by cluster chunk id and grown to
/// <c>newChunkId + 1</c> (<c>ArchetypeClusterState.EnsureClusterAabbsCapacityLocked</c>, called at
/// <c>ArchetypeClusterState.cs:2881</c>), doubling and never shrinking. A flat array therefore costs the id SPACE. A block
/// table in the shape <c>DirtyBitmap</c> uses — 8 words = one 64-byte line = 512 chunk ids per block
/// (<c>DirtyBitmap.cs:33</c>) — costs <c>touchedBlocks × 512</c> instead. The two are equal when the live ids reach into
/// every block, so the number that decides it is <c>touchedBlocks × 512</c> against <c>PrimarySegmentCapacity</c>, not the
/// live count on its own.</para>
/// <para><b>Why the allocator makes the peak, not the total, the thing to fear.</b> <c>ChunkBasedSegment</c> allocates the
/// LOWEST free bit within a page (<c>TrailingZeroCount(~word)</c>, <c>ChunkBasedSegment.cs:578</c>) from a free list of
/// pages that still have room, appended at the TAIL on free (<c>:766</c>, <c>:758</c>), and grows only once two full passes
/// fail and <c>_allocatedCount >= _capacity</c> (<c>:654-672</c>, with <c>GrowIfNeeded</c> returning early while free
/// chunks remain, <c>:426</c>). So churn at a constant population cannot grow the id space: capacity is bounded by PEAK
/// simultaneous live clusters. But <c>_capacity</c> is only ever assigned from <c>ComputeCapacity(length)</c> — fresh
/// <c>:212</c>, load <c>:238</c>, grow <c>:347</c> — and never reduced, so a transient peak sizes the tables permanently,
/// across reopen. That is the exposure this fixture quantifies.</para>
/// <para><b>The grid has to be fine, not the population large.</b> A cluster is a per-CELL bucket, so the live cluster
/// count tracks the number of OCCUPIED CELLS, not the entity count. The first version of this fixture used a 10×10 grid
/// and 6 000 entities: it produced 164 clusters, a whole id space inside a single 512-block, where a block table is
/// necessarily larger than the flat array it replaces and the measurement says nothing. The grid below is 100×100 so the
/// id space spans tens of blocks and the packing question becomes answerable.</para>
/// <para><b>Why the collapse is random.</b> Destroying a random slice is the honest case and the adversarial one: the
/// survivors are left scattered over <c>[0, maxId]</c> rather than packed low, so the touched-block count stays high even
/// as the live count falls. A collapse that destroyed the high ids would flatter the block table for reasons no workload
/// owes us.</para>
/// <para><b>Why the cache is raised and the spawn is batched.</b> At the default 8 MiB the build phase dies in
/// <c>PageCacheBackpressureTimeoutException</c> — measured here at <c>dirty=1024</c>, which is the entire 1 024-page cache
/// gone dirty, and separately at <c>epoch-protected=335</c> when the pinned set rather than the dirty set is the binding
/// constraint. That is #962 (no dirty ceiling, no scan resistance) reproducing on demand, not a defect in this fixture, so
/// it is worked around rather than asserted on: the cache is raised to the production default and the population is built
/// in batches with a fence between them, which is both what keeps epoch-pinned pages down and what a real world does
/// anyway — it grows over ticks, not in one transaction.</para>
/// <para>Reported, not asserted against a threshold — the same stance as <c>ClusterAabbEscapeRateTests</c>. A number that
/// picks a data structure should be read, and a bound invented today would be a guess dressed as a gate. The assertions
/// only pin that the instrument is wired and non-vacuous.</para>
/// </remarks>
[TestFixture]
class ClusterChunkIdCompactionTests : TestBase<ClusterChunkIdCompactionTests>
{
    private const float WorldExtent = 1000f;

    /// <summary>Chunk ids per block, matching <c>DirtyBitmap</c>'s 8 words × 64 bits (<c>DirtyBitmap.cs:33</c>).</summary>
    private const int BlockShift = 9;
    private const int BlockWidth = 1 << BlockShift;

    /// <summary>Page cache for this fixture. The 8 MiB default cannot hold the build phase's working set — see the remarks.</summary>
    private const int LargeCacheSize = 256 * 1024 * 1024;

    /// <summary>Entities per transaction when building or collapsing in bulk; keeps the epoch-pinned page set bounded.</summary>
    private const int BatchSize = 2048;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngine(IServiceScope scope, float cellSize)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), cellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState GetClusterState(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>One observation of the id space: what is live, how far it reaches, and how many 512-blocks it touches.</summary>
    private readonly struct Shape
    {
        public Shape(int live, int capacity, int maxId, int touchedBlocks)
        {
            Live = live;
            Capacity = capacity;
            MaxId = maxId;
            TouchedBlocks = touchedBlocks;
        }

        public int Live { get; }
        public int Capacity { get; }
        public int MaxId { get; }
        public int TouchedBlocks { get; }

        /// <summary>Blocks a perfectly packed id space would need — the floor the block table is measured against.</summary>
        public int IdealBlocks => (Live + BlockWidth - 1) / BlockWidth;

        /// <summary>Blocks a flat array spanning the whole capacity would cover — the ceiling, i.e. "the block table saved nothing".</summary>
        public int CapacityBlocks => (Capacity + BlockWidth - 1) / BlockWidth;

        /// <summary>Entries a block table would hold. Compare against <see cref="Capacity"/>: equal means it saves nothing.</summary>
        public long BlockTableEntries => (long)TouchedBlocks * BlockWidth;

        /// <summary>
        /// Block-table entries against the flat array, as a fraction saved. Negative means the block table is the larger of the two.
        /// </summary>
        /// <remarks>
        /// <b>This is a LOWER BOUND on the real saving, and the gap is not small.</b> The flat baseline here is <c>PrimarySegmentCapacity</c>, but the array
        /// actually being replaced is <c>ClusterAabbs</c>, which <c>PreSizeArchetypeFence</c> sizes to
        /// <c>max(PrimarySegmentCapacity, existingLength) + 2 x PendingMigrationCount + 64</c> and then grows by doubling
        /// (<c>ArchetypeClusterState.cs:612,619-622</c>). So the true flat cost is at least this large and usually larger, and the figures reported below
        /// understate what a block table would save. Conservative in the safe direction — it cannot manufacture a saving that is not there — but a reader
        /// should not take the printed percentage as the estimate.
        /// </remarks>
        public double SavingVersusFlat => Capacity == 0 ? 0d : 1d - ((double)BlockTableEntries / Capacity);

        public double MeanFillPerTouchedBlock => TouchedBlocks == 0 ? 0d : (double)Live / TouchedBlocks;
    }

    private static Shape Measure(ArchetypeClusterState cs)
    {
        // CLUSTERWALK-02's blessed reader — loads count then array with acquire semantics and clamps (ArchetypeClusterState.cs:1640).
        var ids = cs.ReadActiveClusterList(out var count);
        var capacity = cs.PrimarySegmentCapacity;

        var maxId = -1;
        var blocks = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var id = ids[i];
            if (id > maxId)
            {
                maxId = id;
            }
            blocks.Add(id >> BlockShift);
        }

        return new Shape(count, capacity, maxId, blocks.Count);
    }

    private static void SpawnOnce(DatabaseEngine dbe, List<EntityId> live, Random rng, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var x = 1f + ((float)rng.NextDouble() * (WorldExtent - 2f));
            var y = 1f + ((float)rng.NextDouble() * (WorldExtent - 2f));
            live.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
        }
        tx.Commit();
    }

    /// <summary>Destroys <paramref name="count"/> entities chosen uniformly, swap-removing so the survivor set stays unbiased.</summary>
    private static void DestroyOnce(DatabaseEngine dbe, List<EntityId> live, Random rng, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count && live.Count > 0; i++)
        {
            var idx = rng.Next(live.Count);
            tx.Destroy(live[idx]);
            live[idx] = live[^1];
            live.RemoveAt(live.Count - 1);
        }
        tx.Commit();
    }

    private static void SpawnBatched(DatabaseEngine dbe, List<EntityId> live, Random rng, int count, ref int tick)
    {
        for (var done = 0; done < count; done += BatchSize)
        {
            SpawnOnce(dbe, live, rng, Math.Min(BatchSize, count - done));
            dbe.WriteTickFence(tick++);
        }
    }

    private static void DestroyBatched(DatabaseEngine dbe, List<EntityId> live, Random rng, int count, ref int tick)
    {
        for (var done = 0; done < count; done += BatchSize)
        {
            DestroyOnce(dbe, live, rng, Math.Min(BatchSize, count - done));
            dbe.WriteTickFence(tick++);
        }
    }

    private static void Report(string phase, in Shape s) =>
        TestContext.Out.WriteLine(
            $"  {phase,-16} live={s.Live,6}  capacity={s.Capacity,6}  maxId={s.MaxId,6}  " +
            $"blocks={s.TouchedBlocks,4} (ideal {s.IdealBlocks,4}, cap {s.CapacityBlocks,4})  " +
            $"blockTable={s.BlockTableEntries,8}  saving={s.SavingVersusFlat,7:P1}  meanFill={s.MeanFillPerTouchedBlock,6:F1}/{BlockWidth}");

    /// <summary>
    /// Build to a peak, collapse to a steady population, then churn at that population — reporting the id space at each
    /// step. The peak-to-steady drop is the permanent over-sizing; the churn phase tests whether aging adds any more.
    /// </summary>
    private void RunCompactionProfile(float cellSize, int peak, int steady, int churnTicks, double churnFraction, int seed)
    {
        // A fresh scope per profile: a second ConfigureSpatialGrid on an initialised engine is refused, and every phase
        // below has to observe ONE engine's id space for the capacity monotonicity to mean anything.
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, cellSize);

        var rng = new Random(seed);
        var live = new List<EntityId>(peak);
        var tick = 1;

        SpawnBatched(dbe, live, rng, peak, ref tick);
        var cs = GetClusterState(dbe);
        var atPeak = Measure(cs);

        DestroyBatched(dbe, live, rng, peak - steady, ref tick);
        // Several further fences: a drained cluster's chunk is freed on the deferred path, not at commit, and
        // ActiveClusterIds deliberately still lists a drained-but-unfinalized cluster (ArchetypeClusterState.cs:39-41).
        for (var i = 0; i < 4; i++)
        {
            dbe.WriteTickFence(tick++);
        }
        var afterCollapse = Measure(cs);

        var perTick = Math.Max(1, (int)(steady * churnFraction));
        for (var t = 0; t < churnTicks; t++)
        {
            DestroyOnce(dbe, live, rng, perTick);
            SpawnOnce(dbe, live, rng, perTick);
            dbe.WriteTickFence(tick++);
        }
        var afterChurn = Measure(cs);

        var cellsPerAxis = (int)(WorldExtent / cellSize);
        TestContext.Out.WriteLine(
            $"grid={cellsPerAxis}x{cellsPerAxis} (cell {cellSize}), peak={peak} steady={steady}, " +
            $"churn={churnTicks} ticks @ {churnFraction:P0}/tick ({perTick} entities), clusterSize={cs.Layout.ClusterSize}, seed={seed}");
        Report("at peak", in atPeak);
        Report("after collapse", in afterCollapse);
        Report("after churn", in afterChurn);
        TestContext.Out.WriteLine("");

        Assert.Multiple(() =>
        {
            Assert.That(atPeak.Live, Is.GreaterThan(0), "the peak build produced no clusters — the instrument is vacuous");
            Assert.That(afterChurn.Live, Is.GreaterThan(0), "the churn phase ended with no live clusters");
            Assert.That(afterCollapse.Capacity, Is.GreaterThanOrEqualTo(atPeak.Capacity),
                "chunk capacity is assigned only by ComputeCapacity at construction, load and grow — it must never fall");
            Assert.That(afterChurn.Capacity, Is.GreaterThanOrEqualTo(afterCollapse.Capacity),
                "churn at a constant population must not shrink capacity either");
            // NOT "touched >= ideal": distinct 512-blocks covering N ids is >= ceil(N/512) by pigeonhole, so that holds arithmetically whatever Measure does.
            // These two can actually fail — a live id outside the segment's capacity, or more touched blocks than the id space has, both mean the walk read
            // something that is not the live set.
            Assert.That(afterChurn.MaxId, Is.LessThan(afterChurn.Capacity),
                "every live chunk id must lie inside the segment's capacity; outside means the walk is reading garbage");
            Assert.That(afterChurn.TouchedBlocks, Is.LessThanOrEqualTo(afterChurn.CapacityBlocks),
                "the live set cannot touch more blocks than the whole id space contains");
        });
    }

    /// <summary>
    /// The deciding measurement. A 10:1 peak-to-steady collapse is the shape that hurts — a world built large and then
    /// settling — and the churn phase after it answers whether aging compounds the damage or leaves it where the peak put
    /// it. The grid is fine enough that the surviving clusters span many blocks, which is what makes the packing readable.
    /// </summary>
    [Test]
    [Property("CacheSize", LargeCacheSize)]
    [CancelAfter(60_000)]
    public void ChunkIdSpaceUnderAgedChurn()
    {
        RunCompactionProfile(cellSize: 10f, peak: 12_000, steady: 1_200, churnTicks: 30, churnFraction: 0.05, seed: 11);
    }

    /// <summary>
    /// The control: a population that never spikes. If the block table's saving is near zero here and large in the
    /// collapsing case, the problem is the peak and not churn — which is the claim the fixture exists to test.
    /// </summary>
    [Test]
    [Property("CacheSize", LargeCacheSize)]
    [CancelAfter(60_000)]
    public void ChunkIdSpaceAtSteadyPopulation()
    {
        // Same scale as the collapsing arm, so the only difference between them is the peak-to-steady drop. An earlier version held 1 200 entities here,
        // which put the whole id space inside a single 512-block — precisely the regime this fixture's own remarks call unreadable — so the control
        // reported a meaningless -20.5 % and controlled for nothing.
        RunCompactionProfile(cellSize: 10f, peak: 12_000, steady: 12_000, churnTicks: 30, churnFraction: 0.05, seed: 23);
    }
}
