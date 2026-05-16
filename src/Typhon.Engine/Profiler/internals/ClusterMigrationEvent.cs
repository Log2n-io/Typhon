// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.ClusterMigration"/>. Two required fields, no optionals.
/// </summary>
[TraceEvent(TraceEventKind.ClusterMigration, EmitEncoder = true)]
internal ref partial struct ClusterMigrationEvent
{
    [BeginParam]
    public ushort ArchetypeId;
    [BeginParam]
    public int MigrationCount;
    /// <summary>
    /// Total component instances moved across this batch — set by the producer to <c>MigrationCount × archetype.componentCount</c>.
    /// Lets the viewer report data-movement cost (vs. just entity count). Optional at producer site; left at 0 when unset.
    /// </summary>
    [BeginParam]
    public int ComponentCount;

}

/// <summary>
/// Per-tick span around <c>DetectClusterMigrations</c>. Fires once per archetype per tick whenever the spatial path runs, regardless of whether any entity
/// actually crossed a cell — gives the user visibility into the scan cost in workloads where motion stays within hysteresis (AntHill).
/// Outcome counts (migrations queued, hysteresis absorbed) are recorded separately by the existing
/// <see cref="TraceEventKind.SpatialClusterMigrationQueue"/> and <see cref="TraceEventKind.SpatialClusterMigrationHysteresis"/> instants.
/// </summary>
[TraceEvent(TraceEventKind.SpatialClusterMigrationDetectScan, EmitEncoder = true, Gate = "SpatialClusterMigrationDetectActive")]
internal ref partial struct SpatialClusterMigrationDetectScanEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Slots iterated in this scan (== bits set in the dirty/synthetic bitmap passed to DetectClusterMigrations).</summary>
    [BeginParam] public int ScanSlotCount;

    /// <summary>Outcome: slots that crossed a cell boundary and were queued for migration this scan.</summary>
    [Optional(MaskValue = 0x01)] private int _migrationsQueued;
    /// <summary>Outcome: slots that exited the raw cell but stayed within the hysteresis margin — counted but not migrated.</summary>
    [Optional(MaskValue = 0x02)] private int _hysteresisAbsorbed;
    /// <summary>Outcome: distinct clusters with at least one dirty slot — gives concentration vs. spread of activity.</summary>
    [Optional(MaskValue = 0x04)] private int _clustersTouched;
}

/// <summary>
/// Per-tick span around <c>RecomputeDirtyClusterAabbs</c>. Captures the cost of the occupancy scan + bit-exact AABB compare across every active cluster,
/// even when most AABBs end up unchanged. The per-cluster AABB changes are recorded separately by <see cref="TraceEventKind.SpatialCellIndexUpdate"/>.
/// </summary>
[TraceEvent(TraceEventKind.SpatialClusterAabbRefresh, EmitEncoder = true, Gate = "SpatialCellIndexUpdateActive")]
internal ref partial struct SpatialClusterAabbRefreshEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Active clusters scanned this tick (each does an occupancy walk + AABB recompute).</summary>
    [BeginParam] public int ClusterScanned;

    /// <summary>Outcome: clusters whose AABB actually changed and got an UpdateAt + Cell:Index:Update emit.</summary>
    [Optional(MaskValue = 0x01)] private int _aabbsChanged;
    /// <summary>Outcome: total occupancy slots iterated across all scanned clusters — the real O(n) cost driver of the pass.</summary>
    [Optional(MaskValue = 0x02)] private int _slotsScanned;
    /// <summary>Outcome: clusters that hit the max-extent guard and enqueued outlier migrations (rare path).</summary>
    [Optional(MaskValue = 0x04)] private int _outlierGuardFires;
}

