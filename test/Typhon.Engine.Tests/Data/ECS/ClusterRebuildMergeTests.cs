using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════
// #872 step 2 — the merged startup rebuild, diffed against the two-pass pair
// it replaces. The legacy methods are retained as the differential oracle.
// ══════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Rbm.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RbmPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class RbmUnit : Archetype<RbmUnit>
{
    public static readonly Comp<RbmPos> Pos = Register<RbmPos>();
}

[TestFixture]
[NonParallelizable]
class ClusterRebuildMergeTests : TestBase<ClusterRebuildMergeTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RbmPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RbmPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<RbmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Spawns entities spread over several cells, so the rebuild has a non-trivial cluster-to-cell map to reconstruct.</summary>
    private static void SpawnAcrossCells(DatabaseEngine dbe, int perCell, params (float X, float Y)[] cellAnchors)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var (ax, ay) in cellAnchors)
        {
            for (var i = 0; i < perCell; i++)
            {
                // Spread inside the cell so the AABB is a real union rather than a point.
                var dx = i % 8 * 3f;
                var dy = i / 8 % 8 * 3f;
                tx.Spawn<RbmUnit>(RbmUnit.Pos.Set(PointAt(ax + dx, ay + dy)));
            }
        }
        tx.Commit();
    }

    // ── the observable output of a rebuild, captured for comparison ────────

    private sealed class RebuildSnapshot
    {
        public int[] ClusterCellMap;
        public ClusterSpatialAabb[] ClusterAabbs;
        public int[] ClusterSpatialIndexSlot;
        public Dictionary<int, (int ClusterCount, int EntityCount)> Cells;
        public Dictionary<int, int> PerCellIndexCounts;
        public List<(int CellKey, int ClusterId, float MinX, float MinY, float MaxX, float MaxY)> PerCellIndexEntries;
    }

    private static RebuildSnapshot Capture(DatabaseEngine dbe)
    {
        var cs = ClusterState(dbe);
        var snap = new RebuildSnapshot
        {
            ClusterCellMap = (int[])cs.ClusterCellMap.Clone(),
            ClusterAabbs = (ClusterSpatialAabb[])cs.ClusterAabbs.Clone(),
            ClusterSpatialIndexSlot = (int[])cs.ClusterSpatialIndexSlot.Clone(),
            Cells = new Dictionary<int, (int, int)>(),
            PerCellIndexCounts = new Dictionary<int, int>(),
            PerCellIndexEntries = new List<(int, int, float, float, float, float)>(),
        };

        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var cellKey = cs.ClusterCellMap[cs.ActiveClusterIds[i]];
            if (cellKey < 0 || snap.Cells.ContainsKey(cellKey))
            {
                continue;
            }
            ref var cell = ref dbe.SpatialGrid.GetCell(cellKey);
            snap.Cells[cellKey] = (cell.ClusterCount, cell.EntityCount);

            if (cs.PerCellIndex != null && cellKey < cs.PerCellIndex.Length && cs.PerCellIndex[cellKey] != null)
            {
                var slot = cs.PerCellIndex[cellKey];
                snap.PerCellIndexCounts[cellKey] = (slot.DynamicIndex?.ClusterCount ?? 0) + (slot.StaticIndex?.ClusterCount ?? 0);

                // The bounds STORED IN the index, not just how many entries it has — AC-2.1 asks for identical contents, and an index holding the right
                // number of clusters with the wrong boxes would satisfy a count comparison.
                var index = slot.DynamicIndex ?? slot.StaticIndex;
                if (index != null)
                {
                    for (var k = 0; k < index.ClusterCount; k++)
                    {
                        snap.PerCellIndexEntries.Add((cellKey, index.ClusterIds[k], index.MinX[k], index.MinY[k], index.MaxX[k], index.MaxY[k]));
                    }
                }
            }
        }

        return snap;
    }

    private static void AssertIdentical(RebuildSnapshot expected, RebuildSnapshot actual, string what)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.ClusterCellMap, Is.EqualTo(expected.ClusterCellMap), $"{what}: ClusterCellMap");
            Assert.That(actual.ClusterSpatialIndexSlot, Is.EqualTo(expected.ClusterSpatialIndexSlot), $"{what}: ClusterSpatialIndexSlot");
            Assert.That(actual.Cells, Is.EqualTo(expected.Cells), $"{what}: per-cell cluster/entity counts");
            Assert.That(actual.PerCellIndexCounts, Is.EqualTo(expected.PerCellIndexCounts), $"{what}: per-cell index population");
            Assert.That(actual.ClusterAabbs.Length, Is.EqualTo(expected.ClusterAabbs.Length), $"{what}: ClusterAabbs length");
            Assert.That(actual.PerCellIndexEntries, Is.EqualTo(expected.PerCellIndexEntries), $"{what}: per-cell index contents (cell, cluster, bounds)");
        });

        // Bit-identical, not approximately equal: these are the same float operations in the same order, so anything else is a defect.
        for (var i = 0; i < expected.ClusterAabbs.Length; i++)
        {
            var e = expected.ClusterAabbs[i];
            var a = actual.ClusterAabbs[i];
            Assert.That(BitConverter.SingleToInt32Bits(a.MinX), Is.EqualTo(BitConverter.SingleToInt32Bits(e.MinX)), $"{what}: AABB[{i}].MinX");
            Assert.That(BitConverter.SingleToInt32Bits(a.MinY), Is.EqualTo(BitConverter.SingleToInt32Bits(e.MinY)), $"{what}: AABB[{i}].MinY");
            Assert.That(BitConverter.SingleToInt32Bits(a.MaxX), Is.EqualTo(BitConverter.SingleToInt32Bits(e.MaxX)), $"{what}: AABB[{i}].MaxX");
            Assert.That(BitConverter.SingleToInt32Bits(a.MaxY), Is.EqualTo(BitConverter.SingleToInt32Bits(e.MaxY)), $"{what}: AABB[{i}].MaxY");
        }
    }

    /// <summary>
    /// Runs the legacy two-pass rebuild and then the merged one over the SAME data, from a reset grid each time, returning both snapshots.
    /// </summary>
    /// <remarks>
    /// The reset between runs is the whole reason this is a helper: both rebuilds ADD to cell counts and append to the cluster pool (AC-2.3), so running one
    /// after the other on a dirty grid compares a single rebuild against a double-counted one and fails for the wrong reason.
    /// </remarks>
    private static (RebuildSnapshot Oracle, RebuildSnapshot Merged) RunBoth(DatabaseEngine dbe, int maxWorkers)
    {
        var cs = ClusterState(dbe);

        // ChunkBasedSegment.CreateChunkAccessor asserts the CALLING thread is epoch-pinned, so the harness pins exactly as the production caller does
        // (DatabaseEngine.cs:3682). The parallel map's workers pin themselves — this guard does not reach them.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        ResetSpatialState(dbe);
        cs.RebuildCellState(dbe.SpatialGrid);
        cs.RebuildClusterAabbs(dbe.SpatialGrid);
        var oracle = Capture(dbe);

        ResetSpatialState(dbe);
        cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, maxWorkers);
        var merged = Capture(dbe);

        return (oracle, merged);
    }

    /// <summary>Satisfies the documented precondition both rebuilds share: a fresh grid and a fresh per-archetype cluster pool.</summary>
    /// <remarks>
    /// A brand-new pool rather than a clear, mirroring what <c>InitializeSpatial</c> does immediately before the production rebuild
    /// (<c>ArchetypeClusterState.cs:3820</c>). Anything less leaves the append-ordered cluster lists behind and the differential comparison becomes
    /// "one rebuild against two", which is a failure for the wrong reason.
    /// </remarks>
    private static void ResetSpatialState(DatabaseEngine dbe)
    {
        dbe.SpatialGrid.ResetCellState();
        ClusterState(dbe).CellClusterPool = new CellClusterPool(dbe.SpatialGrid.CellCount);
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-2.1 — bit-identical to the two-pass oracle
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void MergedRebuild_MatchesTwoPassOracle_AcrossCellsAndClusters()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnAcrossCells(dbe, perCell: 70, (50f, 50f), (250f, 150f), (650f, 750f));

        var cs = ClusterState(dbe);
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(3), "precondition: several cells, and more than one cluster per cell");

        var (oracle, merged) = RunBoth(dbe, maxWorkers: 1);
        AssertIdentical(oracle, merged, "serial merged vs two-pass oracle");
    }

    [Test]
    public unsafe void MergedRebuild_MatchesOracle_WhenAClusterHasDegenerateBounds()
    {
        // The asymmetry the merge has to preserve: the AABB union SKIPS a slot whose bounds fail validation, while the cell key is taken from the FIRST
        // occupied slot. Corrupting a later slot therefore yields a valid cell key and an AABB that ignores that entity, and the merged pass has to reproduce
        // that rather than "fix" it.
        //
        // Written straight into the cluster bytes because it CANNOT go through the public API: WorldToCellKey rejects a non-finite coordinate at commit. That
        // is the point — ReadAndValidateBoundsFromPtr's validation exists for bytes that did not come through the write path, which is what a startup rebuild
        // reads.
        //
        // INVERTED bounds (MinX > MaxX), not NaN, and in the FIRST occupied slot. Both details are load-bearing:
        //
        //   - NaN in the first slot makes the rebuild THROW, because the cell key is read from that slot with no validation. Real behaviour worth knowing
        //     about — one corrupt entity fails the whole open — but it would test the throw rather than the skip.
        //   - Inverted bounds are degenerate to IsDegenerate yet finite, so the cell key succeeds and only the union rejects them.
        //   - It must be the FIRST slot unioned, because ReadAndValidateBoundsFromPtr validates BEFORE writing `coords`. On a later slot the buffer still holds
        //     the previous (already-unioned) values, so dropping the skip changes nothing and the test would pass against the broken code — which is exactly
        //     what the first version of this test did.
        using var dbe = SetupEngineWithGrid();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20; i++)
            {
                tx.Spawn<RbmUnit>(RbmUnit.Pos.Set(PointAt(50f + i, 50f + i)));
            }
            tx.Commit();
        }

        PokeDegenerateBoundsIntoFirstOccupiedSlot(dbe);

        var (oracle, merged) = RunBoth(dbe, maxWorkers: 1);
        AssertIdentical(oracle, merged, "degenerate first slot");

        // And the behaviour itself is worth pinning, not just the agreement: a shared wrong answer would satisfy AssertIdentical on its own.
        var cs = ClusterState(dbe);
        var chunkId = cs.ActiveClusterIds[0];
        Assert.Multiple(() =>
        {
            Assert.That(merged.ClusterCellMap[chunkId], Is.GreaterThanOrEqualTo(0),
                "inverted bounds are still finite, so the cell key succeeds and the cluster stays mapped");
            Assert.That(merged.ClusterAabbs[chunkId].MinX, Is.EqualTo(51f),
                "the union must SKIP the degenerate slot and start at the second entity — a 0f here means the skip was dropped");
        });
    }

    /// <summary>
    /// Writes an inverted (degenerate but finite) AABB into the first occupied slot of the first active cluster, bypassing write-path validation.
    /// </summary>
    private static unsafe void PokeDegenerateBoundsIntoFirstOccupiedSlot(DatabaseEngine dbe)
    {
        var cs = ClusterState(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var chunkId = cs.ActiveClusterIds[0];
            byte* clusterBase = accessor.GetChunkAddress(chunkId, true);
            ulong occupancy = *(ulong*)clusterBase;
            int slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);

            var ss = cs.SpatialSlot;
            byte* fieldPtr = clusterBase + cs.Layout.ComponentOffset(ss.Slot) + slot * cs.Layout.ComponentSize(ss.Slot) + ss.FieldOffset;
            // MinX > MaxX: degenerate to SpatialGeometry.IsDegenerate, but finite, so the centre (50, 50) still resolves to a cell.
            var f = (float*)fieldPtr;
            f[0] = 60f;   // MinX
            f[1] = 60f;   // MinY
            f[2] = 40f;   // MaxX
            f[3] = 40f;   // MaxY
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    public unsafe void MergedRebuild_MatchesOracle_WhenAClusterIsEmpty()
    {
        // The other case the merge had to preserve, and the one with no coverage until now. An empty cluster is treated ASYMMETRICALLY by the legacy pair:
        // RebuildCellState skips it outright (leaving ClusterCellMap at -1, contributing nothing to the cell counts), while RebuildClusterAabbs still stores
        // ClusterSpatialAabb.Empty for it. The merged reduce reproduces that by writing the AABB unconditionally and only then testing PopCount.
        using var dbe = SetupEngineWithGrid();
        SpawnAcrossCells(dbe, perCell: 70, (50f, 50f), (250f, 150f));

        var cs = ClusterState(dbe);
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(2), "precondition: more than one cluster, so emptying one leaves the rest to compare");

        int emptied;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                emptied = cs.ActiveClusterIds[1];
                *(ulong*)accessor.GetChunkAddress(emptied, true) = 0UL;   // occupancy = 0, still in the active list
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var (oracle, merged) = RunBoth(dbe, maxWorkers: 1);
        AssertIdentical(oracle, merged, "empty cluster");

        Assert.Multiple(() =>
        {
            Assert.That(merged.ClusterCellMap[emptied], Is.EqualTo(-1), "an empty cluster contributes no cell mapping");
            Assert.That(float.IsPositiveInfinity(merged.ClusterAabbs[emptied].MinX), Is.True,
                "but its AABB slot is still written, as Empty — the legacy AABB pass did that unconditionally");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-2.2 — one accessor walk, counted rather than timed
    // ══════════════════════════════════════════════════════════════════════

    [TestCase(1, TestName = "MergedRebuild_ReadsTheClusterSegmentOnce_Serial")]
    [TestCase(8, TestName = "MergedRebuild_ReadsTheClusterSegmentOnce_Parallel")]
    public void MergedRebuild_ReadsTheClusterSegmentOnce(int workers)
    {
        // Asserted at BOTH degrees of parallelism on purpose. An earlier version checked only W=1, which is the one configuration production never uses —
        // the whole point of the step is that the fanned-out path also makes a single pass, and a counter that only holds serially proves nothing about it.
        //
        // The counter measures PASSES, not accessors: the parallel map opens one accessor per partition over a disjoint slice of the cluster range, which is
        // still one pass over the data. Counting accessors would report the worker count and turn this into a test of the scheduler.
        using var dbe = SetupEngineWithGrid();
        SpawnAcrossCells(dbe, perCell: 40, (50f, 50f), (250f, 150f));
        var cs = ClusterState(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        ResetSpatialState(dbe);
        cs.RebuildSegmentPassCount = 0;
        cs.RebuildCellState(dbe.SpatialGrid);
        cs.RebuildClusterAabbs(dbe.SpatialGrid);
        var twoPassReads = cs.RebuildSegmentPassCount;

        ResetSpatialState(dbe);
        cs.RebuildSegmentPassCount = 0;
        cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, workers);
        var mergedReads = cs.RebuildSegmentPassCount;

        Assert.Multiple(() =>
        {
            Assert.That(twoPassReads, Is.EqualTo(2), "precondition: the legacy pair reads the cluster segment once each");
            Assert.That(mergedReads, Is.EqualTo(1), $"the merged rebuild must read the cluster segment exactly once at W={workers}");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-2.3 — the non-idempotency contract is preserved, deliberately
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void MergedRebuild_WithoutReset_StillDoubleCounts()
    {
        // Pinned rather than fixed (#872 AC-2.3). Absorbing the reset would let a caller silently discard a partially-populated grid; the sole production
        // caller allocates a fresh grid and pool immediately beforehand. This test exists so the choice cannot be reversed by accident.
        using var dbe = SetupEngineWithGrid();
        SpawnAcrossCells(dbe, perCell: 20, (50f, 50f));
        var cs = ClusterState(dbe);

        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        ResetSpatialState(dbe);
        cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, maxWorkers: 1);
        var once = Capture(dbe);

        cs.RebuildSpatialStateFromData(dbe.SpatialGrid, dbe.EpochManager, maxWorkers: 1);   // deliberately no reset
        var twice = Capture(dbe);

        var cellKey = dbe.SpatialGrid.WorldToCellKey(50f, 50f, 0f);
        Assert.Multiple(() =>
        {
            Assert.That(twice.Cells[cellKey].EntityCount, Is.EqualTo(once.Cells[cellKey].EntityCount * 2),
                "a second call without a reset must still double-count — the precondition is a caller obligation, not an internal guard");
            Assert.That(twice.Cells[cellKey].ClusterCount, Is.EqualTo(once.Cells[cellKey].ClusterCount * 2));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-2.4 — worker count changes nothing about the result
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(8)]
    public void MergedRebuild_IsIdenticalAtWorkerCount(int workers)
    {
        using var dbe = SetupEngineWithGrid();
        SpawnAcrossCells(dbe, perCell: 70, (50f, 50f), (150f, 50f), (250f, 50f), (350f, 50f), (450f, 50f), (550f, 50f), (650f, 50f), (750f, 50f));

        var cs = ClusterState(dbe);
        // An explicit maxWorkers bypasses ParallelRebuildThreshold by design, so W > 1 really does fan out here rather than quietly running serial and
        // reporting a pass that proves nothing about determinism.
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(8), "precondition: more than one cluster per worker, so scheduling order can actually differ");

        var (oracle, merged) = RunBoth(dbe, maxWorkers: workers);
        AssertIdentical(oracle, merged, $"W={workers} vs two-pass oracle");
    }
}
