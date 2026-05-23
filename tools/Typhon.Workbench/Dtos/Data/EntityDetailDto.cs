namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// Full component-card detail for one entity: every component the archetype declares, with each field decoded. The client
/// joins each field to the cached <c>ComponentSchemaDto</c> (by <c>TypeName</c> + <c>FieldId</c>) for the label and layout.
/// </summary>
/// <param name="EntityId">Raw 64-bit packed entity value as a decimal string (see <see cref="EntityRowDto"/>).</param>
/// <param name="ArchetypeId">The archetype id (numeric, as string).</param>
/// <param name="Revision">The MVCC snapshot (TSN) the entity was read at.</param>
/// <param name="Components">One entry per component slot, in slot order.</param>
public record EntityDetailDto(
    string EntityId,
    string ArchetypeId,
    long Revision,
    ComponentInstanceDto[] Components);

/// <summary>One component instance on an entity — its type name, enabled state, and decoded field values.</summary>
/// <param name="TypeName">Registered component name (matches <c>ComponentSchemaDto.TypeName</c>).</param>
/// <param name="Enabled">The component's EnabledBits state on this entity.</param>
/// <param name="Fields">Decoded field values, one per field of the component.</param>
public record ComponentInstanceDto(
    string TypeName,
    bool Enabled,
    ComponentValueDto[] Fields);

/// <summary>
/// One decoded field value. <paramref name="Value"/> is a JSON-native scalar (number / bool / string) for primitive fields,
/// or <see langword="null"/> for complex/unsupported field types — in which case <paramref name="Raw"/> carries the
/// hex of the field bytes so the value is still inspectable.
/// </summary>
/// <param name="FieldId">The field's id within its component (join key to <c>FieldDto.FieldId</c>).</param>
/// <param name="Value">Decoded scalar, or null for complex types.</param>
/// <param name="Raw">Lowercase hex of the field's raw bytes (always present).</param>
public record ComponentValueDto(
    int FieldId,
    object Value,
    string Raw);
