using System;
using System.Numerics;

namespace SwgTatooine;

/// <summary>
/// Where the spatial layer's density actually landed, per archetype, read off the engine after a run.
/// </summary>
/// <remarks>
/// A per-cell tree can only matter in a cell half holding hundreds of clusters. The tick breakdown says what the queries cost; this says how dense the
/// cells got — the densest half, and how many halves hold at least <see cref="DenseClusters"/> clusters — and how many the promotion gate turned into
/// trees.
/// </remarks>
internal static class SpatialCensus
{
    /// <summary>Clusters from which a half is reported as dense: where a linear scan first costs more than a batch or two.</summary>
    private const int DenseClusters = 64;

    /// <summary>Clusters from which a half's tightness is reported: below it the half is one SIMD batch and its layout barely moves a query's cost.</summary>
    private const int TightClusters = 16;

    internal static void Print(DatabaseEngine dbe)
    {
        Console.WriteLine();
        Console.WriteLine($"  {"archetype",-13} {"halves",7} {"max C",7} {"halves >= " + DenseClusters,13} {"promoted",8}   "
            + $"{"halves >= " + TightClusters,13} {"extent",7} {"ext/bound",9} {"occupancy",9} {"engine/own bound",16} {"clusters",9}");
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        (string Name, int Id)[] archetypes =
        [
            ("WorldObject", Archetype<WorldObject>.Metadata.ArchetypeId), ("CreatureLair", Archetype<CreatureLair>.Metadata.ArchetypeId),
            ("Creature", Archetype<Creature>.Metadata.ArchetypeId), ("CityNpc", Archetype<CityNpc>.Metadata.ArchetypeId),
            ("Player", Archetype<Player>.Metadata.ArchetypeId),
        ];
        foreach (var (name, id) in archetypes)
        {
            Row(dbe, name, id);
        }

        // The last tick's intra-cell maintenance, per archetype: whether the drift gate fired, what it found, and what was done about it.
        Console.WriteLine();
        Console.WriteLine($"  {"last tick",-13} {"gated",7} {"drifters",9} {"unplaced",9} {"spilled",8} {"reloc adm",10} {"throttled",10} {"repairs",8}");
        foreach (var (name, id) in archetypes)
        {
            var t = dbe.GetSpatialTelemetry(id);
            Console.WriteLine($"  {name,-13} {t.DriftGatedClusters,7:N0} {t.DriftersDetected,9:N0} {t.DriftersUnplaced,9:N0} {t.DriftersSpilled,8:N0} "
                + $"{t.RelocationsAdmitted,10:N0} {t.RelocationsThrottled,10:N0} {t.RepairsExecuted,8:N0}");
        }
    }

    /// <summary>
    /// Tightness over the halves a query pays for: mean cluster extent (largest axis, as a fraction of the cell), that extent against the half's packing
    /// bound <c>sqrt(slots / entities)</c> — what a perfect tiling of its own population reaches — and how full its clusters are.
    /// </summary>
    /// <remarks>
    /// The engine/own bound column is the bound the ENGINE derives its gates from — <c>CellState.EntityCount</c>, a sum over every archetype sharing the
    /// cell (#927) — as a fraction of the bound this archetype's own population sets. Below 1, the engine asks this archetype's clusters for a
    /// tightness they cannot reach.
    /// </remarks>
    private static unsafe (int Halves, double Extent, double ToBound, double Occupancy, double EngineBound) Tightness(DatabaseEngine dbe,
        ArchetypeClusterState cs)
    {
        var cellSize = (double)dbe.Realm0Grid.Config.CellSize;
        var slots = BitOperations.PopCount(cs.Layout.FullMask);
        var perCell = cs.Realm0Spatial.PerCellIndex;
        var halves = 0;
        var measured = 0;
        double extentSum = 0d, toBoundSum = 0d, engineBoundSum = 0d;
        long entities = 0, capacity = 0;
        using var accessor = cs.ClusterSegment.CreateChunkAccessor();
        for (var cellKey = 0; cellKey < perCell.Length; cellKey++)
        {
            if (perCell[cellKey] == null)
            {
                continue;
            }

            var ids = cs.Realm0Spatial.CellClusterPool.GetClusters(cellKey);
            if (ids.Length < TightClusters)
            {
                continue;
            }

            double halfExtent = 0d;
            var halfCounted = 0;
            long halfEntities = 0;
            foreach (var id in ids)
            {
                halfEntities += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(id));
                ref var b = ref cs.ClusterAabbs[id];
                if (float.IsPositiveInfinity(b.MinX))
                {
                    continue;
                }

                halfExtent += Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) / cellSize;
                halfCounted++;
            }

            if (halfCounted == 0 || halfEntities == 0)
            {
                continue;
            }

            halves++;
            var bound = Math.Min(1d, Math.Sqrt((double)slots / halfEntities));
            var cellEntities = dbe.Realm0Grid.GetCell(cellKey).EntityCount;
            var engineBound = cellEntities <= slots ? 1d : Math.Sqrt((double)slots / cellEntities);
            extentSum += halfExtent;
            toBoundSum += halfExtent / bound;
            engineBoundSum += engineBound / bound;
            measured += halfCounted;
            entities += halfEntities;
            capacity += (long)ids.Length * slots;
        }

        return measured == 0
            ? (0, 0d, 0d, 0d, 0d)
            : (halves, extentSum / measured, toBoundSum / measured, entities / (double)capacity, engineBoundSum / halves);
    }

    /// <summary>
    /// One cell's <see cref="Tightness"/>: its clusters of this archetype, their mean extent against the packing bound of the cell's population of it, the
    /// mean extent as a fraction of the cell, and slot occupancy. Zero clusters when the cell holds none. Caller holds an epoch.
    /// </summary>
    internal static unsafe (int Clusters, double ToBound, double Extent, double Occupancy) CellTightness(DatabaseEngine dbe, ArchetypeClusterState cs,
        int cellKey)
    {
        var perCell = cs?.Realm0Spatial?.PerCellIndex;
        if (perCell == null || (uint)cellKey >= (uint)perCell.Length || perCell[cellKey] == null)
        {
            return (0, 0d, 0d, 0d);
        }

        var ids = cs.Realm0Spatial.CellClusterPool.GetClusters(cellKey);
        if (ids.Length == 0)
        {
            return (0, 0d, 0d, 0d);
        }

        var cellSize = (double)dbe.Realm0Grid.Config.CellSize;
        var slots = BitOperations.PopCount(cs.Layout.FullMask);
        double extent = 0d;
        var counted = 0;
        long entities = 0;
        using var accessor = cs.ClusterSegment.CreateChunkAccessor();
        foreach (var id in ids)
        {
            entities += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(id));
            ref var b = ref cs.ClusterAabbs[id];
            if (float.IsPositiveInfinity(b.MinX))
            {
                continue;
            }

            extent += Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) / cellSize;
            counted++;
        }

        if (counted == 0 || entities == 0)
        {
            return (ids.Length, 0d, 0d, 0d);
        }

        var mean = extent / counted;
        var bound = Math.Min(1d, Math.Sqrt((double)slots / entities));
        return (ids.Length, mean / bound, mean, entities / (double)((long)ids.Length * slots));
    }

    private static void Row(DatabaseEngine dbe, string name, int archetypeId)
    {
        var cs = dbe._archetypeStates[archetypeId]?.ClusterState;
        var perCell = cs?.Realm0Spatial?.PerCellIndex;
        if (perCell == null)
        {
            Console.WriteLine($"  {name,-13} (no per-cell index)");
            return;
        }

        var halves = 0;
        var maxC = 0;
        var dense = 0;
        foreach (var slot in perCell)
        {
            if (slot == null)
            {
                continue;
            }

            foreach (var c in (ReadOnlySpan<int>)[slot.DynamicClusterCount, slot.StaticClusterCount])
            {
                if (c == 0)
                {
                    continue;
                }

                halves++;
                maxC = Math.Max(maxC, c);
                dense += c >= DenseClusters ? 1 : 0;
            }
        }

        var (tightHalves, extent, toBound, occupancy, engineBound) = Tightness(dbe, cs);
        Console.WriteLine($"  {name,-13} {halves,7:N0} {maxC,7:N0} {dense,13:N0} {cs.Realm0Spatial.PromotedCellCount,8:N0}   "
            + $"{tightHalves,13:N0} {extent,7:F3} {toBound,9:F2} {occupancy,8:P0} {engineBound,16:F2} "
            + $"{dbe.GetSpatialTelemetry(archetypeId).ActiveClusterCount,9:N0}");
    }
}
