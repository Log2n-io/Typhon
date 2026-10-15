using System;

namespace Typhon.Engine.Internals;

/// <summary>Which cluster scan a query takes for one archetype.</summary>
internal enum ClusterScanPath : byte
{
    /// <summary>Let the planner choose on estimated selectivity. The only value in production.</summary>
    Planner = 0,

    /// <summary>Path A — range-scan the archetype's B+Tree for the primary predicate, then verify the rest on the matched slots only.</summary>
    Selective = 1,

    /// <summary>Path B — zone-map-prune each cluster and evaluate every predicate against the SoA column.</summary>
    FullScan = 2
}

/// <summary>Which strategy an unfiltered <c>Count()</c> takes for one archetype.</summary>
internal enum ClusterCountPath : byte
{
    /// <summary>Take the occupancy count when the archetype qualifies. The only value in production.</summary>
    Planner = 0,

    /// <summary>
    /// Prefer the occupancy popcount — which is what <see cref="Planner"/> already does whenever the archetype qualifies, so this is advisory rather than a
    /// force. The qualifying conditions are correctness preconditions, not preferences, and are deliberately NOT overridable: forcing the count past a cluster
    /// the reader cannot vouch for would not exercise a path, it would produce a wrong number.
    /// </summary>
    Occupancy = 1,

    /// <summary>Walk the EntityMap and evaluate the visibility predicate per entity — correct for every shape, and the only option when the fast path bails.</summary>
    MapProbe = 2
}

/// <summary>
/// Test-only control over, and observation of, the Path A / Path B decision in <c>EcsQuery.ScanAllArchetypes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why forcing is necessary and not a shortcut.</b> The choice is made from the primary index's fan-out (<c>EcsQuery.HasFanOutForSelectiveScan</c>), a
/// property of the DATA rather than of the query, so a test that simply runs a selective query and hopes Path A is taken asserts nothing durable: the day a
/// fixture's key distribution shifts it silently becomes a second Path B test, still green, still counted as coverage. Forcing makes "which path ran" an input
/// rather than an accident, which is what lets one fixture assert the two paths agree.
/// </para>
/// <para>
/// <b>Everything here is <see cref="ThreadStaticAttribute"/>, and both reasons matter.</b> For the counters it is contention: plain statics would be written by
/// every query on every thread — a shared cache line updated from 128 cores for the benefit of tests. For <see cref="Forced"/> it is correctness of the tests
/// themselves: NUnit runs this suite's fixtures in parallel, so a process-wide override set by one fixture would silently redirect another fixture's queries
/// onto a path it never asked for, and the resulting failure would be unreproducible in isolation.
/// </para>
/// <para>
/// The cost is a TLS indirection on a branch that runs once per archetype per query — not per entity, and not per row.
/// </para>
/// </remarks>
internal static class QueryPathProbe
{
    /// <summary>Overrides the planner's path choice for the CURRENT THREAD. <see cref="ClusterScanPath.Planner"/> in production; set by tests only.</summary>
    [ThreadStatic]
    internal static ClusterScanPath Forced;

    /// <summary>Archetypes scanned via Path A on this thread since the last <see cref="Reset"/>.</summary>
    [ThreadStatic]
    internal static int SelectiveScans;

    /// <summary>Archetypes scanned via Path B on this thread since the last <see cref="Reset"/>.</summary>
    [ThreadStatic]
    internal static int FullScans;

    /// <summary>
    /// Overrides the unfiltered <c>Count()</c> path for the CURRENT THREAD. <see cref="ClusterCountPath.Planner"/> in production; set by tests only.
    /// </summary>
    /// <remarks>
    /// Same argument as <see cref="Forced"/>, and for the same reason: the occupancy count is taken only when every cluster is fully visible at the reader's
    /// snapshot, which is a property of the data rather than of the query. A test that merely counts and hopes the fast path ran asserts nothing durable —
    /// one tombstone anywhere in the archetype silently turns it into a second map-probe test.
    /// </remarks>
    [ThreadStatic]
    internal static ClusterCountPath ForcedCount;

    /// <summary>Archetypes counted by occupancy popcount on this thread since the last <see cref="Reset"/>.</summary>
    [ThreadStatic]
    internal static int OccupancyCounts;

    /// <summary>Archetypes counted by the per-entity EntityMap probe on this thread since the last <see cref="Reset"/>.</summary>
    [ThreadStatic]
    internal static int MapProbeCounts;

    /// <summary>
    /// Forces a membership-eligible view to refresh by re-querying, for the CURRENT THREAD. False in production; set by tests and profiles only.
    /// </summary>
    /// <remarks>
    /// Same argument as <see cref="Forced"/> and <see cref="ForcedCount"/>. Two uses, and both need it to be a real override rather than a hope: a
    /// differential test asserts the channel and the re-query produce the SAME membership over a randomised workload, which requires running one view both
    /// ways; and the A/B profile behind #790's numbers needs the before-and-after of one view rather than a comparison against a differently-shaped query
    /// that carries costs of its own. Forcing the slow path is always safe — it is a full recomputation, correct for every view shape.
    /// </remarks>
    [ThreadStatic]
    internal static bool ForceViewRequery;

    /// <summary>
    /// Invoked on the commit thread at the instant every membership entry for this commit has been appended and no structural epoch has been bumped yet.
    /// Null in production; set by tests only.
    /// </summary>
    /// <remarks>
    /// MEMB-01 is a publication-ORDER rule, and its failure needs a reader positioned between the bump and the appends. No sequential test can be there, and
    /// racing for the window does not work — the same argument <c>ActiveClusterListPublicationTests</c> makes about the active-cluster pair, where a 40 000-add
    /// spin landed inside a two-instruction window zero times in three runs. So the interleaving is CONSTRUCTED: a verifier hooks this point and asserts the
    /// state the rule promises — entries already in the buffer, epoch not yet moved. Reversing the two makes that assertion fail deterministically, which is
    /// what a rule marked <c>[fatal][silent]</c> needs from its verifier and what a differential over sequential commits cannot give it.
    /// </remarks>
    [ThreadStatic]
    internal static Action MembershipPrePublishBumpHook;


    /// <summary>
    /// Invoked on the publishing thread between a registration's <c>IsDisposed</c> check and its <c>TryAppend</c>. Null in production; set by tests only.
    /// </summary>
    /// <remarks>
    /// This is the window #864 is about, and until this hook existed <c>MEMB-04</c> was <c>verified: NOT COVERED</c> because nothing could schedule a
    /// disposal inside it. Under the previous latch-based design a hook here was USELESS: disposing from it would upgrade shared-to-exclusive on the
    /// publishing thread and self-deadlock, which is why that rule forbade it outright. Deferring the free removes the latch, so a verifier can dispose
    /// the view right here, single-threaded and fully deterministic, and assert the buffer it is about to be written through is still mapped.
    /// </remarks>
    [ThreadStatic]
    internal static Action PrePublishAppendHook;

    /// <summary>
    /// Invoked on the query thread by the selective B+Tree scan between its tree read and its cluster reads, after it read the archetype's destroy stamp
    /// (#1347). Null in production; set by tests only, to commit a destroy the tree read saw and the stamp read did not.
    /// </summary>
    [ThreadStatic]
    internal static Action SelectiveAfterTreeScanHook;

    /// <summary>
    /// Invoked on the query thread by an ordered query after it read each archetype's destroy stamp and before it opens its sorted streams (#1347). Null in
    /// production; set by tests only, to commit a destroy the stamp read did not see and the streams' tree reads will.
    /// </summary>
    [ThreadStatic]
    internal static Action OrderedBeforeStreamsHook;

    /// <summary>
    /// Invoked on the query thread by an ordered query right after it read an archetype's updates, before its streams read their trees (VIS-12). Null in
    /// production; set by tests only, to publish an update that read did not see and the tree reads will.
    /// </summary>
    [ThreadStatic]
    internal static Action OrderedAfterUpdateReadHook;

    /// <summary>
    /// Invoked on the query thread by a snapshot scan before it evaluates a cluster's HEAD, after it read the cluster's update watermark (VIS-12). Null in
    /// production; set by tests only, to publish an update the watermark read did not see and the HEAD read will.
    /// </summary>
    /// <remarks>Read per cluster, so the scans test <see cref="BeforeHeadEvaluateArmed"/> first: a plain static, where a thread-static read on every cluster
    /// cost the no-update scan ~8 %.</remarks>
    internal static Action BeforeHeadEvaluateHook
    {
        get => t_beforeHeadEvaluateHook;
        set
        {
            t_beforeHeadEvaluateHook = value;
            BeforeHeadEvaluateArmed |= value != null;
        }
    }

    [ThreadStatic]
    private static Action t_beforeHeadEvaluateHook;

    /// <summary>Whether any thread ever set <see cref="BeforeHeadEvaluateHook"/>: never in production, so the scans skip the thread-static read.</summary>
    internal static bool BeforeHeadEvaluateArmed;

    /// <summary>Clusters this thread's queries read at their snapshot because an update newer than it rewrote their HEAD or tree keys (VIS-12).</summary>
    [ThreadStatic]
    internal static int SnapshotReadClusters;

    /// <summary>
    /// Walks of every active cluster's update watermark on this thread: an index path whose snapshot is older than its archetype's newest update (VIS-12).
    /// </summary>
    [ThreadStatic]
    internal static int UpdateWatermarkWalks;

    /// <summary>Refreshes of a membership view short-circuited by the structural-epoch gate on this thread since the last <see cref="Reset"/>.</summary>
    /// <remarks>
    /// The gate is the whole point of the membership channel at realistic view counts — most archetypes are untouched on most ticks — and "it was fast" is not
    /// an assertion a test can make durably. Counting the branch makes "nothing was scanned" checkable instead of hoped for.
    /// </remarks>
    [ThreadStatic]
    internal static int MembershipGateHits;

    /// <summary>Refreshes of a membership view that drained the channel on this thread since the last <see cref="Reset"/>.</summary>
    [ThreadStatic]
    internal static int MembershipDrains;

    /// <summary>Refreshes of any view that re-executed the whole query on this thread since the last <see cref="Reset"/> — the pull path, and the membership
    /// path's overflow fallback.</summary>
    [ThreadStatic]
    internal static int ViewRequeries;

    /// <summary>Clear the counters and return both path choices to the planner.</summary>
    internal static void Reset()
    {
        ForceViewRequery = false;
        MembershipPrePublishBumpHook = null;
        PrePublishAppendHook = null;
        SelectiveAfterTreeScanHook = null;
        OrderedBeforeStreamsHook = null;
        OrderedAfterUpdateReadHook = null;
        BeforeHeadEvaluateHook = null;
        MembershipGateHits = 0;
        MembershipDrains = 0;
        ViewRequeries = 0;
        Forced = ClusterScanPath.Planner;
        SelectiveScans = 0;
        FullScans = 0;
        ForcedCount = ClusterCountPath.Planner;
        OccupancyCounts = 0;
        MapProbeCounts = 0;
        SnapshotReadClusters = 0;
        UpdateWatermarkWalks = 0;
    }
}
