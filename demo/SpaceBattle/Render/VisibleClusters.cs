using System;

namespace SpaceBattle;

/// <summary>
/// Resolves "which clusters of this archetype can the camera see" through Typhon's own two-level spatial index.
/// </summary>
/// <remarks>
/// <para>
/// This is the renderer becoming a client of the thing this demo exists to study. The naive alternative — enumerate
/// every cluster of every archetype and reject the ones off screen — costs O(total clusters) per frame no matter how
/// little is visible, which at a tactical view over a 100 km world means walking the whole world to draw a corner of
/// it. Here the camera rectangle goes through exactly the stages a real spatial query goes through:
/// </para>
/// <list type="number">
///   <item><description><b>Level 1.</b> Camera rect to cell range, then only those cells.</description></item>
///   <item><description><b>Level 2.</b> Each visited cell owns a compact SoA of its clusters' AABBs — a linear scan
///   rejects the ones that miss the rect.</description></item>
///   <item><description>The survivors' chunk ids feed <c>GetClusterEnumerator(ids, ...)</c>, so only those clusters
///   are ever opened.</description></item>
/// </list>
/// <para>
/// The counters are the point as much as the culling is. <see cref="ClustersInCells"/> against
/// <see cref="ClustersPassed"/> is the level-2 rejection rate measured on the camera instead of on a synthetic probe
/// — and because it runs every frame on whatever you happen to be looking at, a degenerate cluster AABB shows up as
/// the renderer doing visible work for entities that are nowhere near the screen.
/// </para>
/// <para>
/// Both sub-indexes are scanned. A cell keeps static-mode and dynamic-mode clusters in separate lists (stations sit
/// in one, ships in the other) and checking only the dynamic one would silently drop every immobile object.
/// </para>
/// </remarks>
internal sealed class VisibleClusters
{
    private int[] _ids = new int[512];

    /// <summary>Chunk ids of the clusters that survived, valid for <see cref="Count"/> entries.</summary>
    public int[] Ids => _ids;

    public int Count { get; private set; }

    public int CellsVisited { get; private set; }
    public int ClustersInCells { get; private set; }
    public int ClustersPassed { get; private set; }

    /// <summary>False when the archetype has no per-cell index yet — caller must fall back to a full walk.</summary>
    public bool Resolve(TyphonHost host, int archetypeId, in WorldRect rect)
    {
        Count = 0;
        CellsVisited = 0;
        ClustersInCells = 0;
        ClustersPassed = 0;

        var state = host.ClusterStateOf(archetypeId);
        var perCell = state?.PerCellIndex;
        if (perCell == null)
        {
            return false;
        }

        var g = host.GridConfig;
        var inv = 1f / g.CellSize;
        var cx0 = Math.Clamp((int)MathF.Floor((rect.MinX - g.WorldMin.X) * inv), 0, g.GridWidth - 1);
        var cy0 = Math.Clamp((int)MathF.Floor((rect.MinY - g.WorldMin.Y) * inv), 0, g.GridHeight - 1);
        var cx1 = Math.Clamp((int)MathF.Floor((rect.MaxX - g.WorldMin.X) * inv), 0, g.GridWidth - 1);
        var cy1 = Math.Clamp((int)MathF.Floor((rect.MaxY - g.WorldMin.Y) * inv), 0, g.GridHeight - 1);

        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                CellsVisited++;
                var key = host.Grid.ComputeCellKey(cx, cy);
                if (key < 0 || key >= perCell.Length)
                {
                    continue;
                }
                var slot = perCell[key];
                if (slot == null)
                {
                    continue;
                }
                Scan(slot.DynamicIndex, in rect);
                Scan(slot.StaticIndex, in rect);
            }
        }
        return true;
    }

    private void Scan(CellSpatialIndex index, in WorldRect rect)
    {
        if (index == null || index.ClusterCount == 0)
        {
            return;
        }
        var n = index.ClusterCount;
        ClustersInCells += n;

        var minX = index.MinX;
        var minY = index.MinY;
        var maxX = index.MaxX;
        var maxY = index.MaxY;
        var ids = index.ClusterIds;

        for (var i = 0; i < n; i++)
        {
            if (!rect.Overlaps(minX[i], minY[i], maxX[i], maxY[i]))
            {
                continue;
            }
            ClustersPassed++;
            if (Count == _ids.Length)
            {
                Array.Resize(ref _ids, _ids.Length * 2);
            }
            _ids[Count++] = ids[i];
        }
    }
}
