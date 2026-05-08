namespace Typhon.Profiler;

/// <summary>
/// Rich component-type definition stored in the v7+ <c>ComponentDefinitions</c> table of a <c>.typhon-trace</c> file.
/// Carries the full schema (fields with name + type + offset + size + index flags), storage mode, and per-component
/// aggregates so the Workbench schema panels can render trace sessions with the same fidelity as live engine sessions.
/// </summary>
/// <remarks>
/// The thin <see cref="ComponentTypeRecord"/> (id → name) stays in v7+ for back-compat and for fast id resolution by event decoders; this rich record sits in
/// a separate table after the phases section. Both reference the same engine <c>ComponentTypeId</c> so consumers can join.
/// </remarks>
public sealed class ComponentDefinitionRecord
{
    /// <summary>Component type ID — matches <see cref="ComponentTypeRecord.ComponentTypeId"/>.</summary>
    public int ComponentTypeId { get; init; }

    /// <summary>Display name (CLR full name).</summary>
    public string Name { get; init; }

    /// <summary>Schema revision from <c>[Component(Revision)]</c>.</summary>
    public int Revision { get; init; }

    /// <summary>Storage mode byte (0=Versioned, 1=SingleVersion, 2=Transient — mirrors the engine's <c>StorageMode</c> enum order).</summary>
    public byte StorageMode { get; init; }

    /// <summary>True when multiple instances of this component can attach to the same entity.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Bytes per instance excluding overhead (the user-visible component layout size).</summary>
    public int ComponentStorageSize { get; init; }

    /// <summary>Per-instance overhead (entity-PK + multiple-index element-id slots). 0 for Versioned components.</summary>
    public int ComponentStorageOverhead { get; init; }

    /// <summary>Total per-instance footprint (<see cref="ComponentStorageSize"/> + <see cref="ComponentStorageOverhead"/>).</summary>
    public int ComponentStorageTotalSize { get; init; }

    /// <summary>Number of indexed fields on this component.</summary>
    public ushort IndicesCount { get; init; }

    /// <summary>Number of indexed fields that are multi-valued (require an element-id slot per instance).</summary>
    public ushort MultipleIndicesCount { get; init; }

    /// <summary>Field name with <c>[SpatialIndex]</c>, or empty when none.</summary>
    public string SpatialField { get; init; } = string.Empty;

    /// <summary>Per-field metadata in registration order.</summary>
    public FieldDefinitionRecord[] Fields { get; init; } = [];
}

/// <summary>
/// One field of a <see cref="ComponentDefinitionRecord"/>. Mirrors <c>DBComponentDefinition.Field</c> but carries only what the offline Workbench needs for
/// schema rendering — runtime-only fields (CLR <c>Type</c> handles, foreign-key target types) are not serialised.
/// </summary>
public sealed class FieldDefinitionRecord
{
    /// <summary>Stable field id within the component (used by index keys and tooling).</summary>
    public int FieldId { get; init; }

    /// <summary>Field name.</summary>
    public string Name { get; init; }

    /// <summary>Underlying field-type byte. Wire value matches <c>Typhon.Schema.Definition.FieldType</c>'s enum ordinal.</summary>
    public byte FieldType { get; init; }

    /// <summary>For <c>FieldType.Collection</c>, the element type's enum ordinal; 0 otherwise.</summary>
    public byte UnderlyingType { get; init; }

    /// <summary>Byte offset of the field within the component's storage block.</summary>
    public int Offset { get; init; }

    /// <summary>Field byte size (stride × array length where applicable; otherwise stride).</summary>
    public int Size { get; init; }

    /// <summary>Array length for fixed-size array fields; 0 for scalars.</summary>
    public int ArrayLength { get; init; }

    /// <summary>Bit flags. 0x01 HasIndex, 0x02 IndexAllowMultiple, 0x04 IsIndexAuto, 0x08 HasSpatialIndex, 0x10 IsForeignKey.</summary>
    public byte Flags { get; init; }

    /// <summary>Spatial index parameters — populated only when <see cref="Flags"/> &amp; 0x08. <see cref="SpatialFieldType"/> enum ordinal.</summary>
    public byte SpatialFieldType { get; init; }

    /// <summary>Spatial mode enum ordinal. Populated only when <see cref="Flags"/> &amp; 0x08.</summary>
    public byte SpatialMode { get; init; }

    /// <summary>Spatial cell size (units of the spatial axis). Populated only when <see cref="Flags"/> &amp; 0x08.</summary>
    public float SpatialCellSize { get; init; }

    /// <summary>Spatial margin (extra coverage). Populated only when <see cref="Flags"/> &amp; 0x08.</summary>
    public float SpatialMargin { get; init; }

    /// <summary>Spatial category (uint.MaxValue = unset). Populated only when <see cref="Flags"/> &amp; 0x08.</summary>
    public uint SpatialCategory { get; init; }

    /// <summary>Foreign-key target component name (CLR full name). Empty unless <see cref="Flags"/> &amp; 0x10.</summary>
    public string ForeignKeyTargetType { get; init; } = string.Empty;
}
