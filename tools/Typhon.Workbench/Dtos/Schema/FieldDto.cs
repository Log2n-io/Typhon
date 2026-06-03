namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// One field within a component's byte layout. Offsets and sizes are in bytes within the component storage
/// (excluding the engine-managed EntityPK overhead — see <see cref="ComponentSchemaDto"/>).
/// </summary>
/// <param name="IsSpatial">True when the field carries <c>[SpatialIndex]</c> — i.e. it is the component's position field
/// that <c>SPATIAL</c> queries (NEARBY / AABB / RAY) target. Defaults false; only the live provider populates it (spatial
/// query authoring is a live-session feature).</param>
public record FieldDto(
    string Name,
    string TypeName,
    string TypeFullName,
    int Offset,
    int Size,
    int FieldId,
    bool IsIndexed,
    bool IndexAllowsMultiple,
    bool IsSpatial = false);
