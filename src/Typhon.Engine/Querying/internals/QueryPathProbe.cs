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

    /// <summary>Clear the counters and return both path choices to the planner.</summary>
    internal static void Reset()
    {
        Forced = ClusterScanPath.Planner;
        SelectiveScans = 0;
        FullScans = 0;
        ForcedCount = ClusterCountPath.Planner;
        OccupancyCounts = 0;
        MapProbeCounts = 0;
    }
}
