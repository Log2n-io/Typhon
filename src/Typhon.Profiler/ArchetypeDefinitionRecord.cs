namespace Typhon.Profiler;

/// <summary>
/// Rich archetype definition stored in the v7+ <c>ArchetypeDefinitions</c> table of a <c>.typhon-trace</c> file. Carries the parent/child graph, slot-ordered
/// component IDs, storage-mode bitmasks, cascade-delete edges, and (when cluster-eligible) the inline cluster layout. Drives the Workbench
/// <c>ArchetypeBrowser</c> and relationship view for trace sessions.
/// </summary>
/// <remarks>
/// The thin <see cref="ArchetypeRecord"/> (id → name) stays in v7+ alongside this richer table — they reference the same archetype id, so consumers can join.
/// Naming chosen over re-defining the existing record so wire-format readers that just need id→name resolution aren't forced through the heavier deserialisation path.
/// </remarks>
public sealed class ArchetypeDefinitionRecord
{
    /// <summary>Archetype id (matches <see cref="ArchetypeRecord.ArchetypeId"/>).</summary>
    public ushort ArchetypeId { get; init; }

    /// <summary>Display name (CLR type name).</summary>
    public string Name { get; init; }

    /// <summary>Schema revision from <c>[Archetype(Revision)]</c>.</summary>
    public int Revision { get; init; }

    /// <summary>Parent archetype id, or 0xFFFF for root archetypes.</summary>
    public ushort ParentArchetypeId { get; init; } = 0xFFFF;

    /// <summary>Direct children (immediate subtype archetypes).</summary>
    public ushort[] ChildArchetypeIds { get; init; } = [];

    /// <summary>Total component count (own + inherited). Max 16.</summary>
    public byte ComponentCount { get; init; }

    /// <summary>Slot-ordered component type ids — index N ⇒ component type at slot N. Length == <see cref="ComponentCount"/>.</summary>
    public int[] ComponentTypeIds { get; init; } = [];

    /// <summary>Bit N set ⇒ slot N uses Versioned storage.</summary>
    public ushort VersionedSlotMask { get; init; }

    /// <summary>Bit N set ⇒ slot N uses Transient storage. Slots set in neither mask use SingleVersion.</summary>
    public ushort TransientSlotMask { get; init; }

    /// <summary>Cascade-delete edges: child archetype ids whose entities are auto-destroyed when an entity of this archetype is destroyed.</summary>
    public ushort[] CascadeTargets { get; init; } = [];

    /// <summary>Bit flags. 0x01 IsClusterEligible, 0x02 HasClusterIndexes, 0x04 HasClusterSpatial.</summary>
    public byte Flags { get; init; }

    /// <summary>Per-archetype cluster layout. Non-null only when <see cref="Flags"/> &amp; 0x01 (cluster-eligible).</summary>
    public ArchetypeClusterInfoRecord ClusterInfo { get; init; }
}

/// <summary>
/// Inline cluster-layout descriptor for an archetype's SoA chunk format. Mirrors <c>ArchetypeClusterInfo</c>; serialised inline
/// with each <see cref="ArchetypeDefinitionRecord"/> when the archetype is cluster-eligible.
/// </summary>
public sealed class ArchetypeClusterInfoRecord
{
    /// <summary>Entities per cluster (8–64). Power of 2 in practice but not enforced at the wire level.</summary>
    public ushort ClusterSize { get; init; }

    /// <summary>Total bytes per cluster (header + per-component arrays + element-id tail).</summary>
    public uint ClusterStride { get; init; }

    /// <summary>Bytes of header before the per-component arrays start (8 + 8 × ComponentCount in the engine).</summary>
    public uint HeaderSize { get; init; }

    /// <summary>Byte offset of the entity-id array within the cluster.</summary>
    public uint EntityIdsOffset { get; init; }

    /// <summary>Byte offset of the index-element-id base region within the cluster.</summary>
    public uint IndexElementIdsBaseOffset { get; init; }

    /// <summary>Number of multi-valued indexed fields contributing element-id slots in the cluster tail.</summary>
    public ushort MultipleIndexedFieldCount { get; init; }
}
