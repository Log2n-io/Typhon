namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype per-cell spatial slot holding one <see cref="CellSpatialIndex"/> for each of the static/dynamic splits. Lazily allocated — an entry in
/// <c>ArchetypeClusterState.PerCellIndex</c> is null for any cell where this archetype has no clusters (issue #230, Decision Q10).
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="DynamicIndex"/> and <see cref="StaticIndex"/> are populated independently. An archetype's <see cref="SpatialFieldInfo.Mode"/>
/// determines which one gets written at spawn time (Dynamic → DynamicIndex, Static → StaticIndex), but a single cell may contain clusters of different
/// archetypes with different modes, so both slots are available. Queries check both indexes and union results — this mirrors the pattern used by
/// <c>SpatialIndexState.StaticTree</c> + <c>DynamicTree</c> at the non-cluster level.
/// </para>
/// <para>
/// Issue #230 Phase 3 activated <see cref="StaticIndex"/> as part of closing the issue-body acceptance criterion 7 ("Static/dynamic split: static
/// clusters skip fence updates, queries check both"). Static archetype entities never move, so the tick-fence recompute pass skips StaticIndex entries
/// entirely — the index is written on spawn, updated on destroy, and otherwise read-only.
/// </para>
/// </remarks>
internal sealed class PerCellSpatialSlot
{
    /// <summary>Dynamic-mode cluster index. Populated when clusters with <c>SpatialMode.Dynamic</c> spatial fields are in this cell.</summary>
    public CellSpatialIndex DynamicIndex;

    /// <summary>Static-mode cluster index. Populated when clusters with <c>SpatialMode.Static</c> spatial fields are in this cell. Never tightened by the
    /// tick-fence recompute pass — static entities don't move, so the spawn-time bounds are authoritative.</summary>
    public CellSpatialIndex StaticIndex;
}
