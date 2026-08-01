using System;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Profiler;

/// <summary>
/// Reads spawn/destroy cohorts out of a trace's <see cref="CacheSectionId.EntityLifecycle"/> section (#620, design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// Pure over an <see cref="EntityLifecycleRun"/> array — no session, no engine, no I/O — so the paging and identity rules below are testable directly.
/// The rows arrive sorted by <c>(TickNumber, FirstEntityKey)</c>, which is what makes a tick range a contiguous slice found by binary search rather than
/// a scan.
/// </para>
/// <para>
/// <b>Runs are expanded at the page boundary, never wholesale.</b> A single bulk-load row can describe 200,000 entities; materializing the cohort to
/// take 50 of them would allocate a 1.6 MB array to answer a question about 400 bytes.
/// </para>
/// </remarks>
public static class EntityLifecycleService
{
    /// <summary>Maximum ids returned in one page — the cohort list is a browsing surface, not a bulk export.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Default page size when the caller does not ask for one.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>One tick's spawn or destroy volume — a point on the <c>lifecycle/*</c> track.</summary>
    public readonly record struct LifecyclePoint(uint TickNumber, uint EntityCount, uint RunCount);

    /// <summary>
    /// A page of a spawn/destroy cohort, plus the identity evidence needed to decide whether it may be joined to a database.
    /// </summary>
    /// <param name="TotalEntities">Entities in the whole cohort, not just this page.</param>
    /// <param name="EntityIds">Raw entity ids as decimal strings — they exceed 2^53 and would lose precision as JSON numbers.</param>
    /// <param name="RoutingId">
    /// The durable per-database archetype id shared by every entity in the cohort, or <see cref="MixedRoutingId"/> when the range spans more than one
    /// archetype. A mixed cohort cannot be joined to a single database archetype, and says so rather than picking one.
    /// </param>
    /// <param name="CatalogArchetypeId">
    /// The trace's per-process archetype id, or -1 when unknown. Destroy runs never carry one — the wire event has only the entity id — and a *different*
    /// number from <paramref name="RoutingId"/> for the same archetype whenever registration order differs from persisted routing order (design §5.3).
    /// </param>
    public readonly record struct EntityCohort(
        long TotalEntities,
        int Offset,
        IReadOnlyList<string> EntityIds,
        bool HasMore,
        ushort RoutingId,
        int CatalogArchetypeId,
        uint FromTick,
        uint ToTick);

    /// <summary>Sentinel for <see cref="EntityCohort.RoutingId"/> when a cohort spans multiple archetypes.</summary>
    public const ushort MixedRoutingId = 0xFFFF;

    /// <summary>
    /// Returns one page of the entities whose runs fall in <c>[fromTick, toTick]</c> for the requested kind, optionally narrowed to one archetype's
    /// routing id.
    /// </summary>
    /// <param name="runs">The cache's lifecycle section, sorted by (TickNumber, FirstEntityKey).</param>
    /// <param name="kind">Spawn or destroy.</param>
    /// <param name="fromTick">Inclusive lower tick bound.</param>
    /// <param name="toTick">Inclusive upper tick bound.</param>
    /// <param name="routingIdFilter">When non-null, only runs with this routing id are considered.</param>
    /// <param name="offset">Entities to skip within the cohort.</param>
    /// <param name="limit">Maximum ids to return, clamped to <see cref="MaxPageSize"/>.</param>
    public static EntityCohort GetCohort(
        IReadOnlyList<EntityLifecycleRun> runs,
        EntityLifecycleKind kind,
        uint fromTick,
        uint toTick,
        ushort? routingIdFilter,
        int offset,
        int limit)
    {
        var take = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, MaxPageSize);
        var skip = Math.Max(0, offset);
        var ids = new List<string>(Math.Min(take, 64));

        long total = 0;
        var routingId = MixedRoutingId;
        var routingSeen = false;
        var catalogId = -1;
        var catalogConflict = false;

        if (runs == null || runs.Count == 0 || toTick < fromTick)
        {
            return new EntityCohort(0, skip, Array.Empty<string>(), false, MixedRoutingId, -1, fromTick, toTick);
        }

        for (var i = LowerBoundByTick(runs, fromTick); i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.TickNumber > toTick)
            {
                break;
            }
            if (run.Kind != (byte)kind || (routingIdFilter.HasValue && run.RoutingId != routingIdFilter.Value))
            {
                continue;
            }

            // Identity is accumulated across the whole range, not just the page, so a cohort that mixes archetypes is reported as mixed even when the
            // requested page happens to fall entirely inside one of them.
            if (!routingSeen)
            {
                routingId = run.RoutingId;
                routingSeen = true;
            }
            else if (routingId != run.RoutingId)
            {
                routingId = MixedRoutingId;
            }

            if (run.ArchetypeId != EntityLifecycleRun.UnknownArchetypeId && !catalogConflict)
            {
                if (catalogId < 0)
                {
                    catalogId = run.ArchetypeId;
                }
                else if (catalogId != run.ArchetypeId)
                {
                    catalogId = -1;
                    catalogConflict = true;
                }
            }

            // Expand only the slice of this run that the page actually needs.
            var runStart = total;
            total += run.Count;

            if (ids.Count >= take)
            {
                continue;
            }

            var firstWanted = Math.Max(skip - runStart, 0);
            if (firstWanted >= run.Count)
            {
                continue;
            }

            var available = run.Count - firstWanted;
            var emit = (long)Math.Min(available, take - ids.Count);
            for (long n = 0; n < emit; n++)
            {
                ids.Add(RawIdOf(run.FirstEntityKey + firstWanted + n, run.RoutingId).ToString());
            }
        }

        return new EntityCohort(total, skip, ids, skip + ids.Count < total, routingId, catalogId, fromTick, toTick);
    }

    /// <summary>
    /// Per-tick spawn or destroy volume across <c>[fromTick, toTick]</c>. Only ticks with activity produce a point — the series is sparse, and a
    /// consumer plotting it treats a gap as zero.
    /// </summary>
    public static List<LifecyclePoint> GetSeries(
        IReadOnlyList<EntityLifecycleRun> runs,
        EntityLifecycleKind kind,
        uint fromTick,
        uint toTick,
        ushort? routingIdFilter)
    {
        var points = new List<LifecyclePoint>();
        if (runs == null || runs.Count == 0 || toTick < fromTick)
        {
            return points;
        }

        uint currentTick = 0;
        uint entityCount = 0;
        uint runCount = 0;
        var open = false;

        for (var i = LowerBoundByTick(runs, fromTick); i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.TickNumber > toTick)
            {
                break;
            }
            if (run.Kind != (byte)kind || (routingIdFilter.HasValue && run.RoutingId != routingIdFilter.Value))
            {
                continue;
            }

            if (open && run.TickNumber != currentTick)
            {
                points.Add(new LifecyclePoint(currentTick, entityCount, runCount));
                entityCount = 0;
                runCount = 0;
            }

            currentTick = run.TickNumber;
            open = true;
            entityCount += run.Count;
            runCount++;
        }

        if (open)
        {
            points.Add(new LifecyclePoint(currentTick, entityCount, runCount));
        }

        return points;
    }

    /// <summary>Raw <c>EntityId</c> value for a (key, routingId) pair: the key occupies the high 48 bits.</summary>
    public static long RawIdOf(long entityKey, ushort routingId) => (entityKey << 16) | routingId;

    /// <summary>Index of the first run at or after <paramref name="tick"/>. The section's sort order is what makes this valid.</summary>
    private static int LowerBoundByTick(IReadOnlyList<EntityLifecycleRun> runs, uint tick)
    {
        var lo = 0;
        var hi = runs.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (runs[mid].TickNumber < tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
}
