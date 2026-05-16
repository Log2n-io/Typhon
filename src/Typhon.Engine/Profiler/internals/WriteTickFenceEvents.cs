// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-tick span around the body of the per-ComponentTable loop inside <c>DatabaseEngine.WriteTickFenceCore</c>:
/// WAL serialize + ProcessShadowEntries + ProcessSpatialEntries + DirtyRing.Archive for one dirty SV/Transient table. Surfaces "which table dominated the fence
/// wall?" — the prerequisite breakdown for parallelizing the fence across the worker pool (whole fence runs single-threaded today on the scheduler thread).
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceTable, EmitEncoder = true, Gate = "RuntimeWriteTickFenceTableActive")]
internal ref partial struct WriteTickFenceTableEvent
{
    /// <summary>Component type id — matches <c>ComponentTable.WalTypeId</c>.</summary>
    [BeginParam] public ushort ComponentTypeId;
    /// <summary>Dirty entry count for this table this tick (popcount of <c>DirtyBitmap.Snapshot</c>).</summary>
    [BeginParam] public int DirtyEntryCount;

    /// <summary>Whether the WAL serialize path ran (1 = at least one WAL chunk published, 0 = SV+WAL absent or no entries).</summary>
    [Optional(MaskValue = 0x01)] private byte _walPublished;
    /// <summary>Whether <c>ProcessShadowEntries</c> ran for this table (== <c>table.HasShadowableIndexes</c>).</summary>
    [Optional(MaskValue = 0x02)] private byte _hasShadow;
    /// <summary>Whether <c>ProcessSpatialEntries</c> ran for this table (== <c>table.SpatialIndex.FieldInfo.Mode == Dynamic</c>).</summary>
    [Optional(MaskValue = 0x04)] private byte _hasSpatial;
}

/// <summary>
/// Per-tick span around <c>ProcessShadowEntries</c> for one ComponentTable — deferred index maintenance for non-Versioned indexed fields with shadow buffers.
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceShadow, EmitEncoder = true, Gate = "RuntimeWriteTickFenceShadowActive")]
internal ref partial struct WriteTickFenceShadowEvent
{
    /// <summary>Component type id of the table being processed.</summary>
    [BeginParam] public ushort ComponentTypeId;
    /// <summary>Number of indexed fields on this table (== <c>IndexedFieldInfos.Length</c>).</summary>
    [BeginParam] public int IndexedFieldCount;

    /// <summary>Total shadow buffer entries drained this tick — sum of <c>buffer.Count</c> across all indexed fields.
    /// The real driver of shadow processing cost (per-field Move/MoveValue + view notify).</summary>
    [Optional(MaskValue = 0x01)] private int _totalShadowEntries;
}

/// <summary>
/// Per-tick span around <c>ProcessSpatialEntries</c> for one ComponentTable — R-Tree position update for dirty entities.
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceSpatial, EmitEncoder = true, Gate = "RuntimeWriteTickFenceSpatialActive")]
internal ref partial struct WriteTickFenceSpatialEvent
{
    /// <summary>Component type id of the table being processed.</summary>
    [BeginParam] public ushort ComponentTypeId;
    /// <summary>Dirty entry count iterated by the scan (same snapshot as the WriteTickFenceTable wrapper sees).</summary>
    [BeginParam] public int DirtyEntryCount;

    /// <summary>Outcome: entities whose new position escaped their fat AABB and required a real tree reinsert (the expensive path; the cheap path just updates
    /// the back-pointer leaf in place).</summary>
    [Optional(MaskValue = 0x01)] private int _escapedCount;
}

/// <summary>
/// Per-tick span around the per-archetype body inside <c>WriteClusterTickFence</c> — the cluster-scope mirror of <see cref="WriteTickFenceTableEvent"/>.
/// AntHill and other cluster-backed worlds do all their dirty-entity work here (per-entity <c>DirtyBitmap</c> stays null for cluster-eligible archetypes), so
/// this is the span you need to answer "which archetype dominated the fence wall?" for those workloads. Covers both the has-dirty branch (snapshot + occupancy
/// mask + shadow + spatial + WAL) and the clean branch (dormancy sweep + optional spatial refresh).
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceCluster, EmitEncoder = true, Gate = "RuntimeWriteTickFenceClusterActive")]
internal ref partial struct WriteTickFenceClusterEvent
{
    /// <summary>Archetype id of the cluster-eligible archetype being processed.</summary>
    [BeginParam] public ushort ArchetypeId;

    /// <summary>
    /// Dirty cluster count after occupancy mask (popcount of the masked bitmap). 0 when the clean branch ran. Optional because the count is known mid-body,
    /// after the conditional snapshot — we want to open the span at the top of the per-archetype work so child spans (Shadow/Spatial) parent correctly.
    /// </summary>
    [Optional(MaskValue = 0x01)] private int _dirtyClusterCount;
    /// <summary>Total dirty entity slots iterated (popcount across all dirty cluster bitmaps after occupancy mask).
    /// This is the cost driver for WAL serialize + shadow/spatial passes. Unset on the clean branch.</summary>
    [Optional(MaskValue = 0x02)] private int _entryCount;
    /// <summary>1 when the archetype has cluster-backed index slots and <c>ProcessClusterShadowEntries</c> ran.</summary>
    [Optional(MaskValue = 0x04)] private byte _hasShadow;
    /// <summary>1 when the archetype has a Dynamic spatial slot and the spatial-maintenance block ran.</summary>
    [Optional(MaskValue = 0x08)] private byte _hasSpatial;
    /// <summary>1 when at least one cluster-fence WAL chunk was published this tick for this archetype.</summary>
    [Optional(MaskValue = 0x10)] private byte _walPublished;
}

/// <summary>
/// Per-tick span around <c>ProcessClusterShadowEntries</c> for one cluster-eligible archetype — drains the per-index shadow buffers for cluster-backed B+Tree
/// indexes (analogous to per-table <see cref="WriteTickFenceShadowEvent"/>).
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceClusterShadow, EmitEncoder = true, Gate = "RuntimeWriteTickFenceClusterShadowActive")]
internal ref partial struct WriteTickFenceClusterShadowEvent
{
    /// <summary>Archetype id of the cluster-eligible archetype being processed.</summary>
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Dirty cluster count for this archetype this tick.</summary>
    [BeginParam] public int DirtyClusterCount;

    /// <summary>Total shadow buffer entries drained this tick across all indexed slots — real driver of shadow cost.</summary>
    [Optional(MaskValue = 0x01)] private int _totalShadowEntries;
}

/// <summary>
/// Per-tick span around the cluster spatial-maintenance block: <c>DetectClusterMigrations</c> + <c>ExecuteMigrations</c>
/// + <c>RecomputeDirtyClusterAabbs</c> for one archetype with a Dynamic spatial slot. Parent of the existing fine-grained
/// spans <see cref="TraceEventKind.SpatialClusterMigrationDetectScan"/> + <see cref="TraceEventKind.SpatialClusterAabbRefresh"/>
/// + <see cref="TraceEventKind.ClusterMigration"/>.
/// </summary>
[TraceEvent(TraceEventKind.WriteTickFenceClusterSpatial, EmitEncoder = true, Gate = "RuntimeWriteTickFenceClusterSpatialActive")]
internal ref partial struct WriteTickFenceClusterSpatialEvent
{
    /// <summary>Archetype id of the cluster-eligible archetype being processed.</summary>
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Dirty cluster count fed into the spatial pass (0 on the clean-branch refresh path).</summary>
    [BeginParam] public int DirtyClusterCount;

    /// <summary>Migrations actually executed this tick (== <c>ClusterState.LastTickMigrationCount</c>).</summary>
    [Optional(MaskValue = 0x01)] private int _migrationsExecuted;
}
