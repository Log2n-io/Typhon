using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Step 15 of the VDB cell-grid design (§5.8.5, decision D4): where an entity is placed when it ENTERS a cell — spawn and cell crossing — and the
/// per-cell Morton ordering of batch spawns.
/// </summary>
/// <remarks>
/// <para><b>What was measured.</b> Both entry paths were position-blind: spawn computed the cell from the position and then took the first cluster
/// with a free slot, a cell crossing carried <c>AnyCluster</c> and did the same, and a bulk spawn opened a new cluster only when every existing one
/// was full — so every cluster of a random-order load was born at ~100 % of its cell (bound 63 % at 250 per cell), and the maintenance stack spent its
/// budget undoing that. Least-enlargement placement preserves what repair built (4.5–9.1 points tighter sustained); the Morton ordering is what makes
/// a bulk load born tight; the growth cap is the opt-in that creates tightness at the price of occupancy.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterPlacementTests : TestBase<ClusterPlacementTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine SetupEngine(bool leastEnlargement = false, int sortThreshold = 128, bool growthCap = false, float hysteresis = 0.05f)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // Repair and relocation off: every extent asserted below is placement's alone. The defaults are the SHIPPED ones (least enlargement off).
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            migrationHysteresisRatio: hysteresis,
            clusterTargetExtentRatio: 100f,
            reclusterBudgetMs: 0f,
            clusterTargetPackingSlack: 0f,
            leastEnlargementPlacement: leastEnlargement,
            growthCapPlacement: growthCap,
            batchSpawnSortThreshold: sortThreshold));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Mean and max of the largest-axis extent over the clusters of the cell at grid coordinate (<paramref name="cellX"/>, 0).</summary>
    private static (double mean, double max, int clusters) ExtentsOfCell(DatabaseEngine dbe, int cellX)
    {
        var state = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey((cellX * CellSize) + 50f, 50f, 0f);
        var clusters = state.CellClusterPool.GetClusters(cellKey);
        var total = 0d;
        var max = 0d;
        var counted = 0;
        for (var i = 0; i < clusters.Length; i++)
        {
            var chunkId = clusters[i];
            if ((uint)chunkId >= (uint)state.ClusterAabbs.Length)
            {
                continue;
            }

            ref var box = ref state.ClusterAabbs[chunkId];
            if (float.IsPositiveInfinity(box.MinX))
            {
                continue;
            }

            var extent = MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
            total += extent;
            max = Math.Max(max, extent);
            counted++;
        }

        return (counted == 0 ? 0d : total / counted, max, counted);
    }

    /// <summary>Slots per cluster for this archetype — 49 with these two components, not the 64-bit ceiling.</summary>
    private static int SlotsPerCluster(DatabaseEngine dbe) => System.Numerics.BitOperations.PopCount(ClusterStateOf(dbe).Layout.FullMask);

    /// <summary>Every cluster of the cell with its stored bound, for failure messages.</summary>
    private static string DescribeCell(DatabaseEngine dbe, int cellX)
    {
        var state = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey((cellX * CellSize) + 50f, 50f, 0f);
        var clusters = state.CellClusterPool.GetClusters(cellKey);
        var parts = new List<string>();
        for (var i = 0; i < clusters.Length; i++)
        {
            if ((uint)clusters[i] >= (uint)state.ClusterAabbs.Length)
            {
                parts.Add($"#{clusters[i]} (no box)");
                continue;
            }

            ref var box = ref state.ClusterAabbs[clusters[i]];
            parts.Add($"#{clusters[i]} [{box.MinX:F0},{box.MinY:F0}]-[{box.MaxX:F0},{box.MaxY:F0}]");
        }

        return string.Join(" ", parts);
    }

    private static int EntitiesInCell(DatabaseEngine dbe, int cellX) =>
        dbe.SpatialGrid.TryGetCellKey(cellX, 0, 0, out var key) ? dbe.SpatialGrid.GetCell(key).EntityCount : 0;

    /// <summary>The tags of the cluster holding <paramref name="tag"/>, in slot order — which entities share it, and in what order they were claimed.</summary>
    private static List<int> TagsSharingClusterOf(DatabaseEngine dbe, int tag)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var tags = new List<int>();
                var holdsIt = false;
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var t = cluster.GetReadOnly(ClMigUnit.Pos, slot).Tag;
                    holdsIt |= t == tag;
                    tags.Add(t);
                }

                if (holdsIt)
                {
                    return tags;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return [];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-15.1 — a batch spawn is born at the bound, not at the cell
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 512 entities spawned in one transaction in scrambled geometric order, into one cell: with the Morton ordering on they are born within 1.5× the
    /// packing bound (<c>sqrt(slots / 512)</c> of the cell — a Z-order run of one cluster's worth of points packs at ~1.4× the ideal tiling, which
    /// is also the factor the repair's own Morton re-pack reaches); with it off they are born at the full extent of the cell.
    /// </summary>
    [Test]
    public void ABatchSpawnIsBornAtThePackingBound([Values(true, false)] bool sorted)
    {
        using var dbe = SetupEngine(sortThreshold: sorted ? 128 : 0);
        const int Count = 512;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Count; i++)
            {
                // A multiplicative scramble: consecutive spawns are far apart, so arrival order is the adversarial order for first fit.
                var x = 2f + ((i * 37) % 96);
                var y = 2f + ((i * 61) % 96);
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var (mean, _, clusters) = ExtentsOfCell(dbe, 0);
        var slots = SlotsPerCluster(dbe);
        var bound = CellSize * MathF.Sqrt(slots / (float)Count);

        Assert.Multiple(() =>
        {
            Assert.That(EntitiesInCell(dbe, 0), Is.EqualTo(Count), "the batch did not all land in the cell");
            Assert.That(clusters, Is.EqualTo((Count + slots - 1) / slots), "the ordering must not change how many clusters the batch fills");
            if (sorted)
            {
                Assert.That(mean, Is.LessThanOrEqualTo(bound * 1.5d), $"sorted batch born at {mean:F1} against a bound of {bound:F1}");
            }
            else
            {
                Assert.That(mean, Is.GreaterThan(CellSize * 0.75d), $"unsorted batch born at {mean:F1} — the control arm is not adversarial");
            }
        });
    }

    /// <summary>A batch below the threshold is placed as it comes: the ordering is for loads that can fill more than one cluster of a cell.</summary>
    [Test]
    public void ASmallBatchIsNotReordered()
    {
        using var dbe = SetupEngine(sortThreshold: 128);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(2f + ((i * 37) % 96), 2f + ((i * 61) % 96), i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var slots = SlotsPerCluster(dbe);
        var (_, _, clusters) = ExtentsOfCell(dbe, 0);
        Assert.Multiple(() =>
        {
            Assert.That(clusters, Is.EqualTo((100 + slots - 1) / slots));
            Assert.That(EntitiesInCell(dbe, 0), Is.EqualTo(100));
            // The discriminating check: the sort changes WHICH entities share a cluster. Unsorted, the cursor cluster takes the first `slots` spawns
            // in the order they came — tags 0..slots-1 in slot order; sorted, tag 0's cluster holds its Morton neighbours, scattered over the batch.
            Assert.That(TagsSharingClusterOf(dbe, 0), Is.EqualTo(System.Linq.Enumerable.Range(0, slots)), "the batch was reordered below the threshold");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-15.2 — an arrival lands in the cluster it stretches least
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell with two tight clusters at opposite corners, each with one free slot: a spawn beside the far corner lands there under least enlargement
    /// and beside the cursor's cluster under first fit, stretching it across the cell.
    /// </summary>
    [Test]
    public void ASpawnLandsInTheClusterItStretchesLeast([Values(true, false)] bool leastEnlargement)
    {
        using var dbe = SetupEngine(leastEnlargement, sortThreshold: 0);
        var slots = SlotsPerCluster(dbe);
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < slots; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(5f + (i % 8), 5f + (i / 8), i))));              // corner A, one full cluster
            }

            for (var i = 0; i < slots; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(85f + (i % 8), 85f + (i / 8), slots + i))));   // corner B, one full cluster
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(ExtentsOfCell(dbe, 0).clusters, Is.EqualTo(2), "the two corners must fill exactly two clusters");

        // One free slot in each cluster, then the arrival beside corner B.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Destroy(ids[slots]);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(90f, 90f, 999)));
            tx.Commit();
        }

        dbe.WriteTickFence(3);
        var (_, max, clusters) = ExtentsOfCell(dbe, 0);
        Assert.That(clusters, Is.EqualTo(2), "the arrival must reuse a freed slot, not open a cluster");
        if (leastEnlargement)
        {
            Assert.That(max, Is.LessThan(12d), $"least enlargement should have kept both clusters in their corners; max extent {max:F1}");
        }
        else
        {
            Assert.That(max, Is.GreaterThan(70d), $"first fit was expected to stretch a corner cluster across the cell; max extent {max:F1}");
        }
    }

    /// <summary>
    /// The same choice at a cell crossing: the destination cell holds two tight corner clusters with room, and an entity crossing in beside the far
    /// corner is claimed there by the drain — the destination is chosen at drain time, against the destination cell's live bounds.
    /// </summary>
    [Test]
    public void ACrossingLandsInTheClusterItStretchesLeast([Values(true, false)] bool leastEnlargement)
    {
        using var dbe = SetupEngine(leastEnlargement, sortThreshold: 0);
        var slots = SlotsPerCluster(dbe);
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Both corners FULL, so neither spawn order nor placement policy can mix them; the free slots are opened afterwards.
            for (var i = 0; i < slots; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(105f + (i % 8), 5f + (i / 8), i))));            // cell 1, corner A
            }

            for (var i = 0; i < slots; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(185f + (i % 8), 85f + (i / 8), 100 + i))));     // cell 1, corner B
            }

            ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(95f, 90f, 999))));                                 // cell 0, about to cross
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(ExtentsOfCell(dbe, 1).clusters, Is.EqualTo(2), () => DescribeCell(dbe, 1));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Destroy(ids[slots]);
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        // Cross into cell 1 beside corner B.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (cluster.GetReadOnly(ClMigUnit.Pos, slot).Tag == 999)
                        {
                            cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(190f, 90f, 999));
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

        dbe.WriteTickFence(3);
        var (_, max, clusters) = ExtentsOfCell(dbe, 1);
        Assert.Multiple(() =>
        {
            Assert.That(EntitiesInCell(dbe, 1), Is.EqualTo((2 * (slots - 1)) + 1), "the crossing did not execute");
            Assert.That(clusters, Is.EqualTo(2), "the crosser must reuse a free slot, not open a cluster");
        });

        if (leastEnlargement)
        {
            Assert.That(max, Is.LessThan(12d), $"the drain should have placed the crosser in corner B; max extent {max:F1}: {DescribeCell(dbe, 1)}");
        }
        else
        {
            Assert.That(max, Is.GreaterThan(70d), $"first fit was expected to stretch corner A across the cell; max extent {max:F1}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-15.3 — a stale bound can mis-rank, never mis-cell
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Eight writers spawning into two adjacent cells race eight writers moving entities across the boundary between them through the spatial barrier;
    /// after the fence every entity sits in a cluster mapped to the cell its position resolves to (CC-02, decision C13), and the two cells count what
    /// was spawned. Two cells, not one: with a single live cell the mapped-cell assertion cannot fail, and it is the crossing drain under concurrent
    /// fresh-cluster publication that this is meant to hold to account. Hysteresis is zero so the criterion is exact — with the default 5 % band a
    /// write landing 1.8 units past the boundary is absorbed by design, and the assertion would have to know about the band.
    /// </summary>
    /// <remarks>
    /// Cold-run flake band, not a warm loop: the write-time flag defect it found (a writer reaching a spawn's slot before its data landed, the drain
    /// executing the flag's destination against the spawn's position) showed in about one <c>dotnet test</c> launch in ten and never in 210 warm
    /// in-process iterations. Measure it with launches.
    /// </remarks>
    [Test]
    [VerifiesRule("CC-02")]
    public void ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell([Values(true, false)] bool leastEnlargement)
    {
        using var dbe = SetupEngine(leastEnlargement, sortThreshold: 0, hysteresis: 0f);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 256; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(2f + ((i * 37) % 196), 2f + ((i * 61) % 96), i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        const int Writers = 8;
        const int SpawnsPerWriter = 48;
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task[Writers * 2];
        for (var w = 0; w < Writers; w++)
        {
            var seed = 4300 + w;
            tasks[w] = Task.Run(() =>
            {
                gate.Wait();
                var rng = new Random(seed);
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < SpawnsPerWriter; i++)
                {
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(2f + (float)rng.NextDouble() * 196f, 2f + (float)rng.NextDouble() * 96f, 10_000)));
                }

                tx.Commit();
            });

            tasks[Writers + w] = Task.Run(() =>
            {
                gate.Wait();
                var rng = new Random(seed + 100);
                using var tx = dbe.CreateQuickTransaction();
                var accessor = tx.For<ClMigUnit>();
                try
                {
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var bits = cluster.OccupancyBits;
                        while (bits != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            if ((slot & 7) == (seed & 7))
                            {
                                cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(2f + (float)rng.NextDouble() * 196f, 2f + (float)rng.NextDouble() * 96f));
                            }
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }

                tx.Commit();
            });
        }

        gate.Set();
        Assert.That(Task.WaitAll(tasks, 10_000), Is.True, "a writer hung");
        dbe.WriteTickFence(2);

        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;
        var checkedEntities = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            try
            {
                var mismatches = new List<string>();
                var posSlot = state.SpatialSlot.Slot;
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var mappedCell = state.ClusterCellMap[cluster.ChunkId];
                    var bits = cluster.OccupancyBits;
                    // Every occupied slot's spatial component is enabled: spawns into one cluster from several commits each set their bit on the
                    // cluster's shared word, and a plain read-modify-write there loses bits under exactly this race.
                    if ((cluster.EnabledBits(posSlot) & bits) != bits)
                    {
                        mismatches.Add($"cluster {cluster.ChunkId} lost enabled bits: occupancy {bits:X} enabled {cluster.EnabledBits(posSlot):X}");
                    }

                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        ref readonly var pos = ref cluster.GetReadOnly(ClMigUnit.Pos, slot);
                        var actualCell = grid.WorldToCellKey(pos.Bounds.MinX, pos.Bounds.MinY, 0f);
                        if (actualCell != mappedCell)
                        {
                            mismatches.Add(
                                $"cluster {cluster.ChunkId} is mapped to cell {mappedCell} but slot {slot} (tag {pos.Tag}) sits at ({pos.Bounds.MinX:F1}, {pos.Bounds.MinY:F1}) in cell {actualCell}");
                        }

                        checkedEntities++;
                    }
                }

                if (mismatches.Count > 0)
                {
                    // The failure dump that located both defects this test has found (a stale write-time flag executed at the drain; the pool's
                    // unordered (count, head) pair): which cell list holds each cluster, and per cluster how many of its entities resolve elsewhere.
                    var sb = new System.Text.StringBuilder();
                    for (var c = 0; c < 2; c++)
                    {
                        dbe.SpatialGrid.TryGetCellKey(c, 0, 0, out var key);
                        sb.Append($"cell {key} list: [{string.Join(",", state.CellClusterPool.GetClusters(key).ToArray())}] cursor {state.CellClusterPool.GetScanCursor(key)} count {EntitiesInCell(dbe, c)}\n");
                    }

                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        var mappedCell = state.ClusterCellMap[cluster.ChunkId];
                        var bits = cluster.OccupancyBits;
                        var inCell = 0;
                        var outOfCell = 0;
                        while (bits != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            ref readonly var pos = ref cluster.GetReadOnly(ClMigUnit.Pos, slot);
                            if (grid.WorldToCellKey(pos.Bounds.MinX, pos.Bounds.MinY, 0f) == mappedCell)
                            {
                                inCell++;
                            }
                            else
                            {
                                outOfCell++;
                            }
                        }

                        sb.Append($"#{cluster.ChunkId} cell {mappedCell} occ {System.Numerics.BitOperations.PopCount(cluster.OccupancyBits)} inCell {inCell} outOfCell {outOfCell}\n");
                    }

                    Assert.Fail(string.Join("\n", mismatches) + "\n" + sb);
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(checkedEntities, Is.EqualTo(256 + (Writers * SpawnsPerWriter)), "an entity was lost or duplicated");
            Assert.That(EntitiesInCell(dbe, 0) + EntitiesInCell(dbe, 1), Is.EqualTo(256 + (Writers * SpawnsPerWriter)), "the cells' counts disagree with their clusters");
            Assert.That(EntitiesInCell(dbe, 1), Is.GreaterThan(0), "nothing crossed — the second cell is not exercising the assertion");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Growth cap — the opt-in that creates tightness at birth
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Eight writers spawning into ONE cell, checked before any fence: every cluster the cell opened is in its per-cell index exactly once, the index's
    /// box equals the cluster's (CA-02), and the cluster's box contains every entity it holds (CA-01). Nothing here has had a fence to repair it.
    /// </summary>
    /// <remarks>
    /// Two spawners used to race the "first entity of this cluster" decision — a fresh cluster reached the cell's pool before its index entry existed,
    /// so both read "not indexed", both reset the box (wiping the other's widening) and both added it (a duplicate entry) — and widened the index with
    /// plain stores that a concurrent latched grow of the same index orphaned. A fresh cluster is now indexed by its allocation site under the latch,
    /// and every spawner only ever widens, by CAS, both the cluster's box and its index slot (step 15 review).
    /// </remarks>
    [Test]
    [VerifiesRule("CA-02")]
    public void ConcurrentSpawnsIntoOneCellLeaveTheIndexExactBeforeAnyFence()
    {
        using var dbe = SetupEngine(sortThreshold: 0);
        const int Writers = 8;
        const int SpawnsPerWriter = 64;
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task[Writers];
        for (var w = 0; w < Writers; w++)
        {
            var seed = 7100 + w;
            tasks[w] = Task.Run(() =>
            {
                gate.Wait();
                var rng = new Random(seed);
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < SpawnsPerWriter; i++)
                {
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(2f + (float)rng.NextDouble() * 96f, 2f + (float)rng.NextDouble() * 96f, seed)));
                }

                tx.Commit();
            });
        }

        gate.Set();
        Assert.That(Task.WaitAll(tasks, 10_000), Is.True, "a writer hung");

        var state = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        var pooled = state.CellClusterPool.GetClusters(cellKey).ToArray();
        var index = state.PerCellIndex[cellKey].DynamicIndex;
        var indexed = new List<int>();
        for (var i = 0; i < index.ClusterCount; i++)
        {
            indexed.Add(index.ClusterIds[i]);
        }

        Assert.That(indexed, Is.EquivalentTo(pooled), "the index and the cell's cluster list disagree — a cluster indexed twice, or not at all");
        dbe.SpatialGrid.CellOrigin(cellKey, out var originX, out var originY, out _);

        var checkedEntities = 0;
        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ref var box = ref state.ClusterAabbs[cluster.ChunkId];
                var slot = state.ClusterSpatialIndexSlot[cluster.ChunkId];
                Assert.That(slot, Is.GreaterThanOrEqualTo(0), $"cluster {cluster.ChunkId} has no index slot");
                Assert.That((index.MinX[slot], index.MinY[slot], index.MaxX[slot], index.MaxY[slot]), Is.EqualTo((box.MinX, box.MinY, box.MaxX, box.MaxY)),
                    $"the index box of cluster {cluster.ChunkId} lags its cluster box (CA-02)");

                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var s = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var pos = ref cluster.GetReadOnly(ClMigUnit.Pos, s);
                    var x = pos.Bounds.MinX - originX;
                    var y = pos.Bounds.MinY - originY;
                    Assert.That(x >= box.MinX - 1e-3f && x <= box.MaxX + 1e-3f && y >= box.MinY - 1e-3f && y <= box.MaxY + 1e-3f, Is.True,
                        $"cluster {cluster.ChunkId} slot {s} at ({x:F2}, {y:F2}) is outside its box [{box.MinX:F2},{box.MinY:F2}]-[{box.MaxX:F2},{box.MaxY:F2}] (CA-01)");
                    checkedEntities++;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        Assert.That(checkedEntities, Is.EqualTo(Writers * SpawnsPerWriter));
    }

    /// <summary>
    /// The growth cap is for ARRIVALS into a populated cell — during a bulk fill the cell's live population is still climbing and the cap has no bound
    /// to work against, which is what the Morton ordering is for. Here a sorted fill of 1 024 entities is followed by 200 scrambled arrivals in small
    /// batches: with the cap on, an arrival that would stretch its best candidate past 1.25 × the density target opens a fresh cluster instead, so
    /// no cluster in the cell ends wider than the cap; with it off, first fit keeps filling the one open cluster with both corners' arrivals and
    /// stretches it across the cell, then opens fresh clusters in arrival order.
    /// </summary>
    [Test]
    public void TheGrowthCapKeepsArrivalsUnderTheCapAndPaysInClusters([Values(true, false)] bool growthCap)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // Default floor and slack so the cap has a bound: 1 224 entities in 2D at 49 slots per cluster resolve to a target of 1.5 × sqrt(49 / 1 224)
        // = 0.30 of the cell and a cap of 0.375. Relocation and repair are off (budget 0 is unthrottled relocation, so the target ratio is pushed out of
        // reach instead) — every extent below is placement's alone.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize,
            reclusterBudgetMs: 0f,
            growthCapPlacement: growthCap, batchSpawnSortThreshold: 128));
        dbe.InitializeArchetypes();
        using var _ = dbe;

        const int Fill = 1024;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Fill; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(2f + ((i * 37) % 96), 2f + ((i * 61) % 96), i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var (fillMean, _, fillClusters) = ExtentsOfCell(dbe, 0);
        // The MEAN, not the max: a Z-order run that straddles the top-level quadrant boundary spans most of the cell (measured 92 of 100 for one of
        // 21 clusters), which is Morton's known weakness against Hilbert and a repair-side follow-up, not this test's subject.
        Assert.That(fillMean, Is.LessThan(CellSize * 0.4d), $"the sorted fill was not born tight (mean {fillMean:F1})");

        // 200 arrivals in batches of 50 — below the sort threshold — alternating between two opposite corners, so the fill's partial cluster plus
        // one per corner — three open — hold them tight, under the default open-cluster cap of four. Without the cap, first fit sends the corner-B
        // arrivals into whichever open cluster exists — the corner-A one — and stretches it across the cell.
        for (var batch = 0; batch < 4; batch++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 50; i++)
                {
                    var k = (batch * 50) + i;
                    var corner = (k & 1) == 0 ? 2f : 84f;
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(corner + ((k * 37) % 14), corner + ((k * 61) % 14), 5000 + k)));
                }

                tx.Commit();
            }

            dbe.WriteTickFence(2 + batch);
        }

        var (_, _, clusters) = ExtentsOfCell(dbe, 0);
        var arrivals = MeanExtentOfClustersFrom(dbe, 0, fillClusters);
        Assert.That(EntitiesInCell(dbe, 0), Is.EqualTo(Fill + 200));
        if (growthCap)
        {
            Assert.Multiple(() =>
            {
                Assert.That(arrivals, Is.LessThan(CellSize * 0.3d),
                    $"the clusters the arrivals opened average {arrivals:F1} — the cap should have kept each corner's arrivals in their corner: {DescribeCell(dbe, 0)}");
                Assert.That(clusters, Is.GreaterThan(fillClusters + 1), "tightness is paid for in clusters, and the price must be visible");
            });
        }
        else
        {
            Assert.That(arrivals, Is.GreaterThan(CellSize * 0.7d),
                $"without the cap the clusters first fit opened for scrambled arrivals should span the cell; they average {arrivals:F1}");
        }
    }

    /// <summary>Mean largest-axis extent over the cell's clusters from list index <paramref name="from"/> on — the ones allocated after a fill.</summary>
    private static double MeanExtentOfClustersFrom(DatabaseEngine dbe, int cellX, int from)
    {
        var state = ClusterStateOf(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey((cellX * CellSize) + 50f, 50f, 0f);
        var clusters = state.CellClusterPool.GetClusters(cellKey);
        var total = 0d;
        var counted = 0;
        for (var i = from; i < clusters.Length; i++)
        {
            ref var box = ref state.ClusterAabbs[clusters[i]];
            if (float.IsPositiveInfinity(box.MinX))
            {
                continue;
            }

            total += MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
            counted++;
        }

        return counted == 0 ? 0d : total / counted;
    }
}
