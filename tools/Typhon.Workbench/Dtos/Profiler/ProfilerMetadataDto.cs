namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// Full wire shape returned by <c>GET /api/sessions/{id}/profiler/metadata</c> once the sidecar cache build completes.
/// Everything the Workbench client needs to render the timeline overview + drive chunk fetches.
/// </summary>
/// <param name="Fingerprint">SHA-256 fingerprint of the source file as uppercase hex (64 chars). Used by OPFS chunk cache as subdirectory key.</param>
public record ProfilerMetadataDto(
    string Fingerprint,
    ProfilerHeaderDto Header,
    SystemDefinitionDto[] Systems,
    ArchetypeDto[] Archetypes,
    ComponentTypeDto[] ComponentTypes,
    System.Collections.Generic.Dictionary<int, string> SpanNames,
    GlobalMetricsDto GlobalMetrics,
    TickSummaryDto[] TickSummaries,
    ChunkManifestEntryDto[] ChunkManifest,
    GcSuspensionDto[] GcSuspensions,
    string[] Phases,
    // v12 (#311) — per-system / per-queue / post-tick rollups + queue-name table.
    Typhon.Profiler.SystemTickSummary[] SystemTickSummaries,
    Typhon.Profiler.QueueTickSummary[] QueueTickSummaries,
    Typhon.Profiler.PostTickSummary[] PostTickSummaries,
    System.Collections.Generic.Dictionary<ushort, string> QueueIdToName,
    // v15 (#327) — Workbench Data Flow per-(tick, system, archetype) entity-touch rollups. Empty for v14-or-older caches.
    Typhon.Profiler.SystemArchetypeTouchSummary[] SystemArchetypeTouches);

/// <summary>Header fields projected from <c>TraceFileHeader</c>. All primitive types — JSON-friendly.</summary>
public record ProfilerHeaderDto(
    int Version,
    long TimestampFrequency,
    float BaseTickRate,
    byte WorkerCount,
    ushort SystemCount,
    ushort ArchetypeCount,
    ushort ComponentTypeCount,
    long CreatedUtcTicks,
    long SamplingSessionStartQpc);

/// <summary>One system in the DAG.</summary>
/// <remarks>
/// RFC 07 access declaration fields (<paramref name="PhaseName"/>, <paramref name="Reads"/>, <paramref name="Writes"/>, etc.)
/// are populated from trace v6+ traces and live attach sessions whose engine emits the v6 Init payload. Older v5 traces
/// surface empty arrays for every access field — the wire reader fills empties on the v5 read path so consumers don't
/// need a presence bit.
/// </remarks>
public record SystemDefinitionDto(
    ushort Index,
    string Name,
    byte Type,
    byte Priority,
    bool IsParallel,
    byte TierFilter,
    ushort[] Predecessors,
    ushort[] Successors,
    string PhaseName,
    bool IsExclusivePhase,
    string[] Reads,
    string[] ReadsFresh,
    string[] ReadsSnapshot,
    string[] AdditionalReads,
    string[] Writes,
    string[] SideWrites,
    string[] WritesEvents,
    string[] ReadsEvents,
    string[] WritesResources,
    string[] ReadsResources,
    string[] ExplicitAfter,
    string[] ExplicitBefore);

/// <summary>
/// Archetype-id → metadata mapping. Surfaced through <c>TopologyDto.Archetypes</c> and consumed by the Workbench
/// Data Flow / Access Matrix panels. <c>Label</c> is the user-facing name (<c>ArchetypeAttribute.Alias ?? Name</c>);
/// <c>SchemaRevision</c> mirrors <c>ArchetypeAttribute.Revision</c>; <c>ComponentTypeNames</c> lists the slot-ordered
/// component types declared on the archetype.
/// </summary>
public record ArchetypeDto(
    ushort ArchetypeId,
    string Name,
    string Label,
    int SchemaRevision,
    string[] ComponentTypeNames);

/// <summary>Component-type-id → name mapping.</summary>
public record ComponentTypeDto(int ComponentTypeId, string Name);

/// <summary>Per-tick overview row. Used to render the tick-overview strip at the top of the Profiler panel.</summary>
/// <param name="ActiveSystemsBitmask">64-bit bitmask of active system indices. Serialized as decimal string to preserve precision.</param>
/// <param name="OverloadLevel">From <c>TickEnd</c> payload. 0=Normal, 1=Level1, 2=Level2, 3=TickRateModulation, 4=PlayerShedding. v9+, zero on older traces.</param>
/// <param name="TickMultiplier">Effective rate multiplier (chain: 1, 2, 3, 4, 6). >1 means engine voluntarily throttled. v9+, zero on older traces.</param>
/// <param name="MetronomeWaitUs">Metronome wait duration that PRECEDED this tick (µs, saturated at 65535). v9+, zero on older traces. Issue #289.</param>
/// <param name="MetronomeIntentClass">0=CatchUp, 1=Throttled, 2=Headroom. v9+, zero on older traces.</param>
/// <param name="ConsecutiveOverrun">OverloadDetector's consecutive-overrun streak at end-of-tick. v11+, zero on older.</param>
/// <param name="ConsecutiveUnderrun">OverloadDetector's consecutive-underrun streak at end-of-tick (climbs to <c>DeescalationTicks</c> for deescalation). v11+, zero on older.</param>
public record TickSummaryDto(
    uint TickNumber,
    double StartUs,
    float DurationUs,
    uint EventCount,
    float MaxSystemDurationUs,
    string ActiveSystemsBitmask,
    byte OverloadLevel,
    byte TickMultiplier,
    ushort MetronomeWaitUs,
    byte MetronomeIntentClass,
    ushort ConsecutiveOverrun,
    ushort ConsecutiveUnderrun);

/// <summary>One entry of the chunk manifest — tells the client which chunk covers a given tick range.</summary>
public record ChunkManifestEntryDto(
    uint FromTick,
    uint ToTick,
    uint EventCount,
    bool IsContinuation);

/// <summary>Session-wide aggregate metrics. Computed once during cache build.</summary>
public record GlobalMetricsDto(
    double GlobalStartUs,
    double GlobalEndUs,
    double MaxTickDurationUs,
    double MaxSystemDurationUs,
    double P95TickDurationUs,
    long TotalEvents,
    uint TotalTicks,
    SystemAggregateDto[] SystemAggregates);

/// <summary>Per-system invocation count + total duration, summed across all ticks.</summary>
public record SystemAggregateDto(
    ushort SystemIndex,
    uint InvocationCount,
    double TotalDurationUs);

/// <summary>Single GC suspension instance. Rendered as overlay on the GC gauge track.</summary>
public record GcSuspensionDto(
    double StartUs,
    double DurationUs,
    byte ThreadSlot);
