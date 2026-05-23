namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// A page of entities for one archetype, sliced from a cached snapshot of the archetype's entity ids. The Data Browser's
/// Entity List renders <paramref name="Entities"/> and uses <paramref name="TotalCount"/> to size the virtual scroll.
/// </summary>
/// <param name="ArchetypeId">The archetype these entities belong to (numeric id as string — matches <c>ArchetypeInfoDto.ArchetypeId</c>).</param>
/// <param name="Revision">The MVCC snapshot (TSN) the page was read at.</param>
/// <param name="TotalCount">Total live entities in the archetype at read time — the full snapshot length, not the page length.</param>
/// <param name="Offset">The offset of this page into the snapshot.</param>
/// <param name="Entities">The page rows, in snapshot order.</param>
/// <param name="HasMore">True when <c>Offset + Entities.Length &lt; TotalCount</c>.</param>
public record EntityPageDto(
    string ArchetypeId,
    long Revision,
    long TotalCount,
    int Offset,
    EntityRowDto[] Entities,
    bool HasMore);

/// <summary>
/// One entity row in the list. <paramref name="EntityId"/> is the entity's raw 64-bit packed value as a decimal string
/// (the value exceeds JS <c>Number.MAX_SAFE_INTEGER</c>, so it is never serialized as a JSON number).
/// </summary>
/// <param name="Preview">A small, fixed set of preview field values for the list columns (may be empty in v1).</param>
public record EntityRowDto(
    string EntityId,
    ComponentValueDto[] Preview);
