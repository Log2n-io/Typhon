namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>One point on a <c>lifecycle/&lt;kind&gt;</c> track — entities born or destroyed during a single tick (#620, design §4.4).</summary>
/// <param name="TickNumber">Tick the activity was recorded in. Tick 0 is the synthetic pre-tick bucket where setup-phase spawning lands.</param>
/// <param name="EntityCount">Entities affected this tick.</param>
/// <param name="RunCount">
/// Number of recorded runs behind <paramref name="EntityCount"/>. One batch spawn of 200,000 entities is a single run; 200,000 individual spawns are
/// 200,000 runs. The distinction is what tells a spike caused by one bulk load from a spike caused by a loop.
/// </param>
public record LifecycleTickRecordDto(uint TickNumber, uint EntityCount, uint RunCount);

/// <summary>
/// A page of the entities spawned (or destroyed) inside a tick range, plus the identity evidence needed before joining them to a database.
/// </summary>
/// <param name="Kind"><c>spawn</c> or <c>destroy</c>.</param>
/// <param name="TotalEntities">Entities in the whole cohort, not just this page.</param>
/// <param name="EntityIds">
/// Raw entity ids as decimal <b>strings</b>. They are 64-bit and routinely exceed 2^53, so JSON numbers would silently round them — and a rounded entity
/// id is a valid-looking id for a different entity.
/// </param>
/// <param name="RoutingId">
/// The durable per-database archetype id shared by every entity in the cohort, or null when the range spans more than one archetype. This is the
/// identifier that is safe to join against the database (design §5.2).
/// </param>
/// <param name="CatalogArchetypeId">
/// The capture's per-process archetype id, or null when unknown — destroy runs never carry one. It is a <b>different number from
/// <paramref name="RoutingId"/> for the same archetype</b> whenever registration order differs from persisted routing order, which is design §5.3's
/// landmine. Surfaced for display only; never join on it.
/// </param>
/// <param name="ArchetypeName">
/// Display name resolved from <paramref name="RoutingId"/> via the capture's archetype table, or null when the cohort is mixed or the capture predates
/// the routing-id field. Null means <i>unknown</i>, never <i>unnamed</i>.
/// </param>
public record EntityCohortDto(
    string Kind,
    uint FromTick,
    uint ToTick,
    long TotalEntities,
    int Offset,
    string[] EntityIds,
    bool HasMore,
    ushort? RoutingId,
    int? CatalogArchetypeId,
    string ArchetypeName);

/// <summary>
/// How much of a spawn cohort the database still holds — the "1,240 spawned here, 830 still alive" answer (#620, design §4.4).
/// </summary>
/// <param name="RoutingId">The archetype's durable routing id, as the database reports it. Every id in <paramref name="AliveIds"/> embeds this value.</param>
/// <param name="Revision">
/// TSN of the read the answer was computed against. A cohort comes from a past capture; this states which present it was compared to.
/// </param>
/// <param name="MissingIds">
/// Ids the database does not hold. Because entity ids are never recycled, "missing" means destroyed or never committed — never "belongs to something else
/// now".
/// </param>
/// <param name="ForeignRoutingCount">
/// Ids whose embedded routing id was not this archetype's. Reported separately from <paramref name="MissingIds"/> on purpose: they are evidence of a
/// mis-joined cohort, and folding them into "missing" would render a wrong join as a mass extinction.
/// </param>
public record CohortResolutionDto(
    string ArchetypeId,
    ushort RoutingId,
    long Revision,
    string[] AliveIds,
    string[] MissingIds,
    int ForeignRoutingCount);

/// <summary>Request body for resolving or paging an explicit cohort. Sent as a body because an id list does not fit a query string.</summary>
public record CohortRequestDto(string[] EntityIds);
