using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// One cluster a spatial query's broadphase admitted, reported without reading a single entity inside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the answer a caller wants when its unit of interest is the cluster, not the entity.</b> A cluster is a spatially tight group of at most 64
/// entities, so "which clusters does this query reach" is the same question as "which entities" at a coarser grain — about 28 times coarser on a dense
/// world — and a caller that can act on a whole cluster at once has no reason to pay the narrowphase that splits it back into entities.
/// </para>
/// <para>
/// <b>It over-approximates, and that is the contract.</b> Every entity the entity-level query would report lives in a cluster reported here, because a
/// cluster's bounds enclose its entities; the converse does not hold, since a cluster straddling the query boundary is reported whole. A caller that needs
/// the exact set runs the narrowphase on the clusters this reports; a caller that can afford the over-approximation never touches an entity at all.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct ClusterBroadphaseHit
{
    internal ClusterBroadphaseHit(int chunkId, ulong slots, double minX, double minY, double maxX, double maxY)
    {
        ChunkId = chunkId;
        Slots = slots;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    /// <summary>The cluster's chunk id.</summary>
    public int ChunkId { get; }

    /// <summary>Its occupied slots, as the occupancy word read when the cluster was opened.</summary>
    public ulong Slots { get; }

    /// <summary>World-space minimum X of the cluster's tight bounds.</summary>
    public double MinX { get; }

    /// <summary>World-space minimum Y.</summary>
    public double MinY { get; }

    /// <summary>World-space maximum X.</summary>
    public double MaxX { get; }

    /// <summary>World-space maximum Y.</summary>
    public double MaxY { get; }
}
