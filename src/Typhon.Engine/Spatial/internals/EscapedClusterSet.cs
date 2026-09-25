using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The few clusters of one archetype whose box reaches further outside their own cell than <see cref="ArchetypeClusterState.ClusterReach"/> covers — the
/// outliers a cell-walking query visits by name instead of widening every walk to reach them (SQ-01).
/// </summary>
/// <remarks>
/// <para><b>Why a list and not a wider walk.</b> A cluster is filed by its entities' centres, so its box can leave its cell; every query must still reach
/// it. Widening each query's cell range by the largest reach any cluster has makes one outlier — an entity teleported across the world and not yet
/// migrated, a stale box a migration left behind — the cost of every query of the archetype: measured at ~50x the cells walked (SWG Tatooine, 2026-09-13).
/// The reach therefore covers the ordinary clusters only, and the outliers are named here, tested against each query's box directly.</para>
/// <para><b>Immutable once published.</b> <see cref="ArchetypeClusterState.RefreshClusterReach"/> builds a new instance at the fence and publishes it with
/// a release store; a query captures one reference at construction and reads a consistent set for its whole life. Bounds are WORLD f64 (SQ-06): the
/// cluster's C15 cell-relative box widened exactly through its home cell's origin.</para>
/// <para><b>Chunk ids can go stale between fences.</b> A cluster drained and freed outside the fence can have its id reused in another cell. Every
/// consumer checks <see cref="IsCurrent"/> before opening an entry, which rejects a reused id: opening it would report the new cluster's entities a second
/// time, once here and once through their own cell.</para>
/// </remarks>
internal sealed class EscapedClusterSet
{
    /// <summary>At most this many clusters are named; past it, the reach widens to cover the rest. One AVX-512 pass of f64 lanes is eight.</summary>
    internal const int Capacity = 16;

    internal static readonly EscapedClusterSet Empty = new(0);

    public readonly int Count;
    public readonly int[] ChunkIds;
    public readonly int[] HomeCellKeys;
    public readonly int[] CellX;
    public readonly int[] CellY;
    public readonly int[] CellZ;
    public readonly double[] MinX;
    public readonly double[] MinY;
    public readonly double[] MinZ;
    public readonly double[] MaxX;
    public readonly double[] MaxY;
    public readonly double[] MaxZ;
    public readonly uint[] CategoryMasks;

    internal EscapedClusterSet(int count)
    {
        Count = count;
        ChunkIds = new int[count];
        HomeCellKeys = new int[count];
        CellX = new int[count];
        CellY = new int[count];
        CellZ = new int[count];
        MinX = new double[count];
        MinY = new double[count];
        MinZ = new double[count];
        MaxX = new double[count];
        MaxY = new double[count];
        MaxZ = new double[count];
        CategoryMasks = new uint[count];
    }

    /// <summary>Is entry <paramref name="i"/>'s home cell inside the cell range a walk covers? Then the walk opens it, and it must not be opened twice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HomeCellIn(int i, int cellMinX, int cellMinY, int cellMinZ, int cellMaxX, int cellMaxY, int cellMaxZ) =>
        CellX[i] >= cellMinX && CellX[i] <= cellMaxX && CellY[i] >= cellMinY && CellY[i] <= cellMaxY && CellZ[i] >= cellMinZ && CellZ[i] <= cellMaxZ;

    /// <summary>Does entry <paramref name="i"/>'s world box overlap a world box? A 2D entry carries ±Infinity on Z, which any 2D query overlaps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Overlaps(int i, double minX, double minY, double minZ, double maxX, double maxY, double maxZ) =>
        MaxX[i] >= minX && MinX[i] <= maxX && MaxY[i] >= minY && MinY[i] <= maxY && MaxZ[i] >= minZ && MinZ[i] <= maxZ;

    /// <summary>
    /// Does a query need entry <paramref name="i"/> by name — its world box overlaps the query's, and its home cell is outside the range the query's own
    /// cell walk covers? The single query and each member of a radius batch ask exactly this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Reaches(int i, in QueryGeometry query, int cellMinX, int cellMinY, int cellMinZ, int cellMaxX, int cellMaxY, int cellMaxZ) =>
        Overlaps(i, query.MinX, query.MinY, query.MinZ, query.MaxX, query.MaxY, query.MaxZ)
        && !HomeCellIn(i, cellMinX, cellMinY, cellMinZ, cellMaxX, cellMaxY, cellMaxZ);

    /// <summary>
    /// Is entry <paramref name="i"/> still the cluster it named — same chunk id, still filed under the same cell OF THE SAME REALM? A freed chunk id can be
    /// recycled into another realm's cell carrying the same key (Realms C1), and a query that opened it would answer with another realm's entities.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCurrent(int i, int[] clusterCellMap, ushort[] clusterRealmMap, RealmId realm)
    {
        int id = ChunkIds[i];
        return clusterCellMap != null && (uint)id < (uint)clusterCellMap.Length && clusterCellMap[id] == HomeCellKeys[i]
               && clusterRealmMap != null && (uint)id < (uint)clusterRealmMap.Length && clusterRealmMap[id] == realm.Value;
    }
}
