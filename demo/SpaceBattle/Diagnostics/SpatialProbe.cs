using System;
using System.Collections.Generic;

namespace SpaceBattle;


/// <summary>
/// Measures broadphase <em>selectivity</em> — the number R1 asked for in March 2026 and that has never existed:
/// <c>"what is the percentage of useful data they process in a query?"</c>
/// </summary>
/// <remarks>
/// <para>
/// It reproduces exactly what <c>AabbClusterEnumerator</c> does — derive the cell range from the query bounds, walk
/// the clusters homed in those cells, reject on cluster AABB, then narrowphase the survivors — but counts every
/// stage. It works entirely off the public <c>ClusterRef.SpatialBounds</c> plus the cluster→cell map, so it needs no
/// engine modification; the engine's own enumerator exposes no counters.
/// </para>
/// <para>
/// The headline figure is <see cref="Selectivity"/> = matched / examined. A broadphase filter's entire value is its
/// rejection rate: at 90 % it is a 10× win, at 0 % it is pure overhead plus the cost of keeping it accurate.
/// </para>
/// </remarks>
internal sealed class SpatialProbe
{
    public int CellsTouched;
    public int ClustersInCells;
    public int ClustersPassedAabb;
    public int EntitiesExamined;      // entities living in clusters that passed the broadphase
    public int EntitiesMatched;       // entities actually inside the query
    public float TotalClusterArea;
    public float CellArea;

    /// <summary>Fraction of examined entities that were actually wanted. 1.0 = perfect, 0.0 = the filter did nothing.</summary>
    public float Selectivity => EntitiesExamined > 0 ? EntitiesMatched / (float)EntitiesExamined : 0f;

    /// <summary>Fraction of candidate clusters the AABB test rejected.</summary>
    public float ClusterRejectRate => ClustersInCells > 0 ? 1f - ClustersPassedAabb / (float)ClustersInCells : 0f;

    /// <summary>Mean cluster-AABB area as a fraction of one cell's area. ~1.0 means "cluster ≈ cell" — level 2 is
    /// carrying no information level 1 didn't already have.</summary>
    public float MeanClusterAreaVsCell => ClustersPassedAabb > 0 && CellArea > 0
        ? TotalClusterArea / ClustersPassedAabb / CellArea
        : 0f;

    public void Reset()
    {
        CellsTouched = 0;
        ClustersInCells = 0;
        ClustersPassedAabb = 0;
        EntitiesExamined = 0;
        EntitiesMatched = 0;
        TotalClusterArea = 0;
    }

    /// <summary>
    /// Runs an AABB probe over the cluster boxes the renderer already gathered this frame.
    /// </summary>
    public void Measure(TyphonHost host, IReadOnlyList<ClusterBox> boxes, int archetypeId,
                        float qx0, float qy0, float qx1, float qy1)
    {
        Reset();
        var g = host.GridConfig;
        CellArea = g.CellSize * g.CellSize;

        // Stage 1 — cell range, exactly as WorldToCellRange computes it (raw bounds, no margin).
        var cx0 = (int)MathF.Floor((qx0 - g.WorldMin.X) / g.CellSize);
        var cy0 = (int)MathF.Floor((qy0 - g.WorldMin.Y) / g.CellSize);
        var cx1 = (int)MathF.Floor((qx1 - g.WorldMin.X) / g.CellSize);
        var cy1 = (int)MathF.Floor((qy1 - g.WorldMin.Y) / g.CellSize);
        cx0 = Math.Clamp(cx0, 0, g.GridWidth - 1);
        cy0 = Math.Clamp(cy0, 0, g.GridHeight - 1);
        cx1 = Math.Clamp(cx1, 0, g.GridWidth - 1);
        cy1 = Math.Clamp(cy1, 0, g.GridHeight - 1);

        var touched = new HashSet<int>();
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                touched.Add(host.Grid.ComputeCellKey(cx, cy));
            }
        }
        CellsTouched = touched.Count;

        // Stages 2 and 3 — candidate clusters in those cells, then the AABB rejection test.
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (b.ArchetypeId != archetypeId || b.HomeCellKey < 0 || !touched.Contains(b.HomeCellKey))
            {
                continue;
            }
            ClustersInCells++;
            if (!b.Overlaps(qx0, qy0, qx1, qy1))
            {
                continue;
            }
            ClustersPassedAabb++;
            EntitiesExamined += b.LiveCount;
            TotalClusterArea += b.Area;
        }
    }

    /// <summary>
    /// Stage 4 — the true answer, from the engine's own query. Run after <see cref="Measure"/> so
    /// <see cref="Selectivity"/> compares like with like.
    /// </summary>
    public void MeasureMatches(TyphonHost host, float qx0, float qy0, float qx1, float qy1)
    {
        var box = new Typhon.Schema.Definition.AABB2F { MinX = qx0, MinY = qy0, MaxX = qx1, MaxY = qy1 };
        using var tr = host.DBE.CreateQuickTransaction();
        var q = host.DBE.ClusterSpatialQuery<Ship>().AABB<Typhon.Schema.Definition.AABB2F>(in box);
        try
        {
            var n = 0;
            while (q.MoveNext())
            {
                n++;
            }
            EntitiesMatched = n;
        }
        finally
        {
            q.Dispose();
        }
    }
}
