using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The archetype's query-efficiency tally (#906, rule SO-02): what its range queries tested against what they matched, per tick.
/// </summary>
internal sealed partial class ArchetypeClusterState
{
    // Created by the first query that opens a cluster: most archetypes of a test engine are never queried, and a tally is 14 KB.
    private SpatialQueryTally _queryTally;

    // The totals the previous fence read, so the next one publishes only what ran since.
    private long _queryClustersSeen;
    private long _queryCandidatesSeen;
    private long _queryHitsSeen;

    /// <summary>Clusters this archetype's range queries opened since the previous fence. Written by the fence's per-tick reset.</summary>
    internal long LastTickQueryClustersOpened;

    /// <summary>Entities those queries tested. Written by the fence's per-tick reset.</summary>
    internal long LastTickQueryCandidates;

    /// <summary>Matches those queries returned. Written by the fence's per-tick reset.</summary>
    internal long LastTickQueryHits;

    /// <summary>The tally, or null while no query has opened one of this archetype's clusters.</summary>
    internal SpatialQueryTally QueryTally => Volatile.Read(ref _queryTally);

    /// <summary>
    /// Add one range query's totals, from the thread it ran on: <paramref name="threadId"/> is that thread's managed id. Called once per query, when it
    /// hands its page window back.
    /// </summary>
    internal void RecordQueryTally(int threadId, int clustersOpened, int candidates, int hits) =>
        (Volatile.Read(ref _queryTally) ?? CreateQueryTally()).Add(threadId, clustersOpened, candidates, hits);

    private SpatialQueryTally CreateQueryTally()
    {
        var created = new SpatialQueryTally();
        return Interlocked.CompareExchange(ref _queryTally, created, null) ?? created;
    }

    /// <summary>
    /// Publish what ran since the previous call as this tick's query counters. Called once per archetype per fence, from its per-tick reset, which runs
    /// after the tick's systems and before anything reads the counters.
    /// </summary>
    internal void TakeQueryTallyDelta()
    {
        var tally = Volatile.Read(ref _queryTally);
        if (tally == null)
        {
            LastTickQueryClustersOpened = 0;
            LastTickQueryCandidates = 0;
            LastTickQueryHits = 0;
            return;
        }

        tally.Read(out var clusters, out var candidates, out var hits);
        LastTickQueryClustersOpened = clusters - _queryClustersSeen;
        LastTickQueryCandidates = candidates - _queryCandidatesSeen;
        LastTickQueryHits = hits - _queryHitsSeen;
        _queryClustersSeen = clusters;
        _queryCandidatesSeen = candidates;
        _queryHitsSeen = hits;
    }
}
