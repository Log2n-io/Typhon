namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// Triage-friendly summary for the Schema Browser — one row per registered component type. Powers column-based sorting
/// (name, size, field count, entity count, index count) and quick-filter chips ("no entities", "no indexes", "large").
/// </summary>
/// <param name="ArchetypeCount">
/// Number of archetypes containing this component. Nullable — Phase 1 returns null until the ArchetypeRegistry accessor
/// ships in Phase 2 (issue #256). The Browser column is hidden when every row has null.
/// </param>
/// <param name="StorageMode">
/// The component's MVCC storage mode — "Versioned", "SingleVersion" or "Transient" (GAP-25). Sourced from the engine
/// (live) or the trace's recorded component definition. The Schema Explorer / Archetype Inspector surface it per type.
/// </param>
/// <param name="HasSpatialIndex">
/// True when one of the component's fields carries <c>[SpatialIndex]</c>. The Query Console's SPATIAL chip uses this to
/// filter its component picker to spatial-capable components. Defaults false; only the live provider populates it.
/// </param>
public record ComponentSummaryDto(
    string TypeName,
    string FullName,
    int StorageSize,
    int FieldCount,
    int? ArchetypeCount,
    int EntityCount,
    int IndexCount,
    string StorageMode,
    bool HasSpatialIndex = false);
