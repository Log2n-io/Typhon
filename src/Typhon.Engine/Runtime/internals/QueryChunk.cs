namespace Typhon.Engine.Internals;

/// <summary>
/// What one chunk of a parallel QuerySystem's dispatch walks, decided once by the runtime's prepare (CD-02, CD-03): its range of the cluster list Prepare
/// counted, and its slice of the entity list Prepare materialized, when the dispatch path has one.
/// </summary>
/// <remarks>
/// The two are split independently — the cluster range over the cluster count, the entity slice over the entity count — so they do not describe the same
/// entities; a system uses one or the other. Aligning them would change how evenly work is shared and is a separate decision (#1110 D4).
/// </remarks>
internal struct QueryChunk
{
    public int ClusterStart;
    public int ClusterEnd;
    public int EntityStart;
    public int EntityCount;
}
