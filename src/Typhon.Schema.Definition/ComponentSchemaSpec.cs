using JetBrains.Annotations;
using System;

namespace Typhon.Schema.Definition;

/// <summary>
/// Implemented by source-generated <c>[Component]</c> structs to supply their schema shape to the engine without runtime reflection.
/// The engine dispatches to <see cref="GetComponentSchema"/> in place of reflecting over the struct's fields and attributes; the returned
/// <see cref="ComponentSchemaSpec"/> is pure data (offsets are computed once via <see cref="System.Runtime.InteropServices.Marshal.OffsetOf(System.Type,string)"/>
/// inside the generated method — the only residual runtime call).
/// </summary>
/// <remarks>
/// The generator emits an explicit-interface implementation on the component's <c>partial struct</c>. The engine reaches it by boxing
/// <c>default(T)</c> once at registration time (never on a hot path). Hand-authored implementations are equally valid and drive the same
/// engine build core that reflection does, so a source-generated component and a reflected one produce byte-identical definitions.
/// </remarks>
[PublicAPI]
public interface IComponentSchemaProvider
{
    /// <summary>Returns the reflection-free schema description for this component.</summary>
    /// <returns>The component's <see cref="ComponentSchemaSpec"/>.</returns>
    ComponentSchemaSpec GetComponentSchema();
}

/// <summary>
/// Pure-data description of one component revision's schema: its identity, storage/durability defaults, and ordered fields.
/// Consumed by the engine's single build core (shared by reflection and the source generator) to produce the compiled component definition.
/// </summary>
[PublicAPI]
public readonly struct ComponentSchemaSpec
{
    /// <summary>Component name — the stable schema identity (from <see cref="ComponentAttribute.Name"/>).</summary>
    public string Name { get; }

    /// <summary>Component schema revision (from <see cref="ComponentAttribute.Revision"/>).</summary>
    public int Revision { get; }

    /// <summary>Storage mode for this component (from <see cref="ComponentAttribute.StorageMode"/>).</summary>
    public StorageMode StorageMode { get; }

    /// <summary>Default durability discipline (from <see cref="ComponentAttribute.DefaultDiscipline"/>); only meaningful for <see cref="StorageMode.SingleVersion"/>.</summary>
    public DurabilityDiscipline DefaultDiscipline { get; }

    /// <summary>The component's fields in declaration order (parent-first for inherited layouts). Never null.</summary>
    public ComponentFieldSpec[] Fields { get; }

    /// <summary>Creates a component schema spec.</summary>
    /// <param name="name">Component name (see <see cref="Name"/>).</param>
    /// <param name="revision">Schema revision (see <see cref="Revision"/>).</param>
    /// <param name="fields">Ordered field specs (see <see cref="Fields"/>).</param>
    /// <param name="storageMode">Storage mode (see <see cref="StorageMode"/>); default <see cref="StorageMode.Versioned"/>.</param>
    /// <param name="defaultDiscipline">Default durability discipline (see <see cref="DefaultDiscipline"/>); default <see cref="DurabilityDiscipline.TickFence"/>.</param>
    public ComponentSchemaSpec(
        string name,
        int revision,
        ComponentFieldSpec[] fields,
        StorageMode storageMode = StorageMode.Versioned,
        DurabilityDiscipline defaultDiscipline = DurabilityDiscipline.TickFence)
    {
        Name = name;
        Revision = revision;
        Fields = fields;
        StorageMode = storageMode;
        DefaultDiscipline = defaultDiscipline;
    }
}

/// <summary>
/// Pure-data description of a single component field: its identity, CLR type, byte offset, and any array / index / foreign-key / spatial metadata.
/// The engine maps <see cref="DotNetType"/> to its stored field type and applies field-id resolution (schema migration) when building the definition,
/// so this spec carries the same inputs reflection would read from the struct — never a pre-resolved field id.
/// </summary>
[PublicAPI]
public readonly struct ComponentFieldSpec
{
    /// <summary>Field name — the schema-match key (from <see cref="FieldAttribute.Name"/> or the C# field name).</summary>
    public string Name { get; }

    /// <summary>Former field name, set when the field was renamed so the persisted field can be carried forward (from <see cref="FieldAttribute.PreviousName"/>); null when never renamed.</summary>
    public string PreviousName { get; }

    /// <summary>Explicit stable field id (from <see cref="FieldAttribute.FieldId"/>), or null to let the engine assign / resolve it.</summary>
    public int? ExplicitFieldId { get; }

    /// <summary>The backing CLR type of the field (e.g. <c>typeof(float)</c>). The engine maps this to its stored field type.</summary>
    public Type DotNetType { get; }

    /// <summary>Byte offset of the field within the component's storage, computed once via <see cref="System.Runtime.InteropServices.Marshal.OffsetOf(System.Type,string)"/>.</summary>
    public int Offset { get; }

    /// <summary>True for a schema-static field — carried on the definition but excluded from per-entity storage and the field-id layout.</summary>
    public bool IsStatic { get; }

    /// <summary>Fixed element count when the field is an array; <c>0</c> for a scalar field.</summary>
    public int ArrayLength { get; }

    /// <summary>True when the field carries a scalar index (<see cref="IndexAttribute"/>).</summary>
    public bool HasIndex { get; }

    /// <summary>True when the index permits multiple entities per key (non-unique). Only meaningful when <see cref="HasIndex"/> is true.</summary>
    public bool IndexAllowMultiple { get; }

    /// <summary>True when the field is a foreign key (<see cref="ForeignKeyAttribute"/>) referencing another component's entities.</summary>
    public bool IsForeignKey { get; }

    /// <summary>The target component type the foreign key points at (from <see cref="ForeignKeyAttribute.TargetComponentType"/>); null unless <see cref="IsForeignKey"/> is true.</summary>
    public Type ForeignKeyTargetType { get; }

    /// <summary>True when the field carries a spatial index (<see cref="SpatialIndexAttribute"/>). At most one spatial field is allowed per component.</summary>
    public bool HasSpatialIndex { get; }

    /// <summary>Fat-AABB movement-hysteresis margin (from <see cref="SpatialIndexAttribute.Margin"/>). Meaningful when <see cref="HasSpatialIndex"/>.</summary>
    public float SpatialMargin { get; }

    /// <summary>Broadphase cell size (from <see cref="SpatialIndexAttribute.CellSize"/>); <c>0</c> selects the engine default. Meaningful when <see cref="HasSpatialIndex"/>.</summary>
    public float SpatialCellSize { get; }

    /// <summary>Whether the spatial index is static or dynamic (from <see cref="SpatialIndexAttribute.Mode"/>). Meaningful when <see cref="HasSpatialIndex"/>.</summary>
    public SpatialMode SpatialMode { get; }

    /// <summary>Archetype-level category bitmask for spatial broadphase filtering (from <see cref="SpatialIndexAttribute.Category"/>). Meaningful when <see cref="HasSpatialIndex"/>.</summary>
    public uint SpatialCategory { get; }

    /// <summary>Creates a component field spec. Only <paramref name="name"/>, <paramref name="dotNetType"/>, and <paramref name="offset"/> are required; the remaining metadata defaults to "absent".</summary>
    /// <param name="name">Field name (see <see cref="Name"/>).</param>
    /// <param name="dotNetType">Backing CLR type (see <see cref="DotNetType"/>).</param>
    /// <param name="offset">Byte offset within component storage (see <see cref="Offset"/>).</param>
    /// <param name="previousName">Former field name (see <see cref="PreviousName"/>).</param>
    /// <param name="explicitFieldId">Explicit field id (see <see cref="ExplicitFieldId"/>).</param>
    /// <param name="isStatic">Schema-static flag (see <see cref="IsStatic"/>).</param>
    /// <param name="arrayLength">Fixed array length (see <see cref="ArrayLength"/>).</param>
    /// <param name="hasIndex">Scalar-index flag (see <see cref="HasIndex"/>).</param>
    /// <param name="indexAllowMultiple">Non-unique index flag (see <see cref="IndexAllowMultiple"/>).</param>
    /// <param name="isForeignKey">Foreign-key flag (see <see cref="IsForeignKey"/>).</param>
    /// <param name="foreignKeyTargetType">Foreign-key target type (see <see cref="ForeignKeyTargetType"/>).</param>
    /// <param name="hasSpatialIndex">Spatial-index flag (see <see cref="HasSpatialIndex"/>).</param>
    /// <param name="spatialMargin">Spatial margin (see <see cref="SpatialMargin"/>).</param>
    /// <param name="spatialCellSize">Spatial cell size (see <see cref="SpatialCellSize"/>).</param>
    /// <param name="spatialMode">Spatial mode (see <see cref="SpatialMode"/>).</param>
    /// <param name="spatialCategory">Spatial category bitmask (see <see cref="SpatialCategory"/>).</param>
    public ComponentFieldSpec(
        string name,
        Type dotNetType,
        int offset,
        string previousName = null,
        int? explicitFieldId = null,
        bool isStatic = false,
        int arrayLength = 0,
        bool hasIndex = false,
        bool indexAllowMultiple = false,
        bool isForeignKey = false,
        Type foreignKeyTargetType = null,
        bool hasSpatialIndex = false,
        float spatialMargin = 0f,
        float spatialCellSize = 0f,
        SpatialMode spatialMode = SpatialMode.Dynamic,
        uint spatialCategory = uint.MaxValue)
    {
        Name = name;
        DotNetType = dotNetType;
        Offset = offset;
        PreviousName = previousName;
        ExplicitFieldId = explicitFieldId;
        IsStatic = isStatic;
        ArrayLength = arrayLength;
        HasIndex = hasIndex;
        IndexAllowMultiple = indexAllowMultiple;
        IsForeignKey = isForeignKey;
        ForeignKeyTargetType = foreignKeyTargetType;
        HasSpatialIndex = hasSpatialIndex;
        SpatialMargin = spatialMargin;
        SpatialCellSize = spatialCellSize;
        SpatialMode = spatialMode;
        SpatialCategory = spatialCategory;
    }
}
