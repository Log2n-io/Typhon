namespace Typhon.Engine.Internals;

/// <summary>
/// Discriminator for <see cref="FenceWorkItem"/>. The fence is split into three chained phases; each phase has its own kind. The Prep and Finalize kinds are
/// archetype-atomic (one item per cluster-eligible archetype, runs end-to-end on a single worker). MigrationApply is sliceable — a single archetype's
/// <c>PendingMigrations</c> array is partitioned into contiguous slices sorted by destination cell key so multiple workers can apply migrations for the SAME
/// archetype concurrently without contending on cell-level data.
/// </summary>
internal enum FenceWorkKind : byte
{
    /// <summary>Run a whole archetype's tick fence end-to-end (Prepare + ExecuteMigrations + Finalize) on one worker.
    /// Used in the opt-out / serial path and as a fallback for archetypes that cannot be split.</summary>
    ArchetypeAtomic = 1,

    /// <summary>Run <see cref="DatabaseEngine.ProcessTableFence"/> end-to-end for one component table.</summary>
    TableAtomic = 2,

    /// <summary>Phase 1 — per-archetype Prep work: snapshot bitmap, occupancy mask, shadow, zone-maps, detect migrations.
    /// One item per cluster-eligible archetype. Order-tight; must finish before any <see cref="MigrationApply"/> on the same archetype starts.</summary>
    ArchetypePrep = 3,

    /// <summary>Phase 2 — apply a contiguous slice of one archetype's <c>PendingMigrations</c>. Multiple slices per fat archetype; each worker owns a disjoint
    /// cell-key range so dst-side mutations are worker-exclusive.</summary>
    MigrationApply = 4,

    /// <summary>Phase 3 — apply a contiguous slice of one archetype's AABB recompute. Slicing axis depends on the archetype's iteration mode:
    /// SpatialBarrierOnly mode slices <c>ClusterProcessBitmap</c> by word range; Legacy mode slices <c>ActiveClusterIds</c> by index range. Multiple slices per
    /// fat archetype enable per-archetype parallel AABB recompute. Must run AFTER all <see cref="MigrationApply"/> slices for the same archetype have completed.
    /// </summary>
    AabbRefreshSlice = 6,

    /// <summary>Phase 4 — per-archetype Finalize work: bookkeeping clear, dormancy sweep, dirty-ring archive, WAL emit. One item per cluster-eligible archetype.
    /// Must run AFTER all <see cref="AabbRefreshSlice"/> slices for the same archetype have completed.</summary>
    ArchetypeFinalize = 5,
}

/// <summary>
/// Per-migration dirty-bit delta produced by <see cref="DatabaseEngine.ExecuteMigrations"/> and accumulated in a worker-local buffer. Flushed under the
/// archetype's <c>_finalizeLock</c> at chunk end, never written directly to the shared <c>FenceDirtyBits</c> from the hot loop — avoids false sharing on
/// adjacent cluster chunkIds (8 longs per cache line means concurrent <c>Interlocked.Or</c> on neighbor chunkIds ping-pong the line).
/// </summary>
internal struct DirtyBitDelta
{
    public ushort ArchetypeId;
    public int SrcChunkId;
    public long SrcClearMask;
    public int DstChunkId;
    public long DstSetMask;
}

/// <summary>
/// Single unit of work emitted by the fence planner and consumed by the chained FenceExec systems. <see cref="Cost"/> is unitless and produced by the cost
/// model in <see cref="LiveFenceCostModel"/>; the bin-packer uses it for chunk balancing only. <see cref="SliceStart"/> / <see cref="SliceCount"/> are
/// populated only by sliceable kinds; <see cref="UnitCount"/> carries the cost-attribution unit count (entities for MigrationApply, clusters for
/// AabbRefreshSlice) consumed by <see cref="LiveFenceCostModel"/> to compute µs-per-unit from measured wall time.
/// </summary>
internal struct FenceWorkItem
{
    public FenceWorkKind Kind;
    public int TargetId;       // archetypeId for Archetype*/MigrationApply kinds; component WalTypeId for TableAtomic
    public float Cost;
    public int SliceStart;     // PendingMigrations index where this slice begins (MigrationApply only)
    public int SliceCount;     // number of migrations in this slice (MigrationApply only)
    public int UnitCount;      // cost-attribution unit count: entities for MigrationApply, clusters for AabbRefreshSlice; 0 otherwise
}
