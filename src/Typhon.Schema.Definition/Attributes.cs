using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Typhon.Engine")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Schema.Definition;

/// <summary>
/// Marks a struct as an ECS component type and carries its schema identity (<see cref="Name"/>, <see cref="Revision"/>) plus its storage and durability
/// defaults. The annotated struct must be blittable (unmanaged) so components can be stored and persisted zero-copy.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
[PublicAPI]
public sealed class ComponentAttribute : Attribute
{
    /// <summary>Registered component name — the stable schema identity persisted with the data and matched by name when the database is reopened.</summary>
    public string Name { get; }

    /// <summary>Component schema revision. Increment when the field set changes; migrations only run forward, so the target revision must exceed the persisted one.</summary>
    public int Revision { get; }

    /// <summary>Former <see cref="Name"/> of this component, set when it is renamed so the persisted schema can be matched to the new name on reopen. <c>null</c> when never renamed.</summary>
    public string PreviousName { get; set; }

    /// <summary>Storage mode for this component. Default is <see cref="StorageMode.Versioned"/> (full MVCC).</summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Versioned;

    /// <summary>
    /// Default durability discipline for this component when its <see cref="StorageMode"/> is <see cref="StorageMode.SingleVersion"/>.
    /// Default is <see cref="DurabilityDiscipline.TickFence"/> (batched, ≤1-tick loss).
    /// Set to <see cref="DurabilityDiscipline.Commit"/> to make any transaction that writes this component commit-durable (zero-loss, atomic) for all of its
    /// writes (CM-02 uniformity). Ignored for <see cref="StorageMode.Versioned"/> (always commit-scoped) and <see cref="StorageMode.Transient"/> (never durable).
    /// </summary>
    public DurabilityDiscipline DefaultDiscipline { get; set; } = DurabilityDiscipline.TickFence;

    /// <summary>Declares a component with the given schema <paramref name="name"/> and <paramref name="revision"/>.</summary>
    /// <param name="name">Registered component name (see <see cref="Name"/>).</param>
    /// <param name="revision">Schema revision (see <see cref="Revision"/>).</param>
    public ComponentAttribute(string name, int revision)
    {
        Name = name;
        Revision = revision;
    }
}

/// <summary>
/// Overrides the schema identity of a component field. Optional — an unannotated field is still a schema field, keyed by its C# name with an auto-assigned id.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class FieldAttribute : Attribute
{
    /// <summary>
    /// Explicit stable identifier for this field, persisted and used to match fields across schema revisions. <c>null</c> lets the schema assign the next free
    /// id, resolving the field against the persisted schema by <see cref="Name"/> then <see cref="PreviousName"/>.
    /// </summary>
    public int? FieldId { get; set; }

    /// <summary>Overrides the persisted field name (defaults to the C# field name). Used as the schema-match key when the database is reopened.</summary>
    public string Name { get; set; }

    /// <summary>Former <see cref="Name"/> of this field, set when it is renamed so the persisted field can be matched and carried forward. <c>null</c> when never renamed.</summary>
    public string PreviousName { get; set; }
}

/// <summary>Cascade action when a parent entity is deleted.</summary>
[PublicAPI]
public enum CascadeAction
{
    /// <summary>No cascade — children are unaffected.</summary>
    None = 0,

    /// <summary>Delete all children whose FK points to the destroyed parent.</summary>
    Delete = 1,
}

/// <summary>Marks a component field for indexing, maintaining a B+Tree index over the field's values for lookups and range scans.</summary>
[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class IndexAttribute : Attribute
{
    /// <summary><c>true</c> for a non-unique index (many entities may share a key value); <c>false</c> (default) for a unique index (one entity per key).</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>
    /// Cascade action when the parent entity (referenced by an <c>EntityLink&lt;T&gt;</c> FK field) is deleted.
    /// Only applicable to indexed EntityLink fields. Default is <see cref="CascadeAction.None"/>.
    /// </summary>
    public CascadeAction OnParentDelete { get; set; } = CascadeAction.None;
}

/// <summary>
/// Declares a component field as a foreign key referencing another component type, enabling FK validation and cascade-on-delete behavior
/// (see <see cref="IndexAttribute.OnParentDelete"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class ForeignKeyAttribute : Attribute
{
    /// <summary>The component type this foreign key references.</summary>
    public Type TargetComponentType { get; }

    /// <summary>Declares a foreign key referencing <paramref name="targetComponentType"/>.</summary>
    /// <param name="targetComponentType">The referenced component type (see <see cref="TargetComponentType"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="targetComponentType"/> is <c>null</c>.</exception>
    public ForeignKeyAttribute(Type targetComponentType)
    {
        ArgumentNullException.ThrowIfNull(targetComponentType);
        TargetComponentType = targetComponentType;
    }
}

/// <summary>
/// Marks a class as an ECS archetype with a globally unique, immutable identifier.
/// The Id is embedded in every EntityId (12-bit field, max 4095) and must never change once assigned.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[PublicAPI]
public sealed class ArchetypeAttribute : Attribute
{
    /// <summary>Globally unique archetype identifier (0-4095). Embedded in persisted EntityIds — immutable once assigned.</summary>
    public ushort Id { get; }

    /// <summary>Schema revision. Increment when the component set changes (add/remove components).</summary>
    public int Revision { get; }

    /// <summary>
    /// Optional human-readable label used by the Workbench (Data Flow Timeline, Access Matrix, System DAG side panel).
    /// Falls back to the declaring type name when null. Has no effect on persisted EntityIds.
    /// </summary>
    public string Alias { get; }

    /// <summary>Declares an archetype with the given globally unique <paramref name="id"/>.</summary>
    /// <param name="id">Globally unique archetype identifier, 0-4095 (see <see cref="Id"/>). Immutable once assigned.</param>
    /// <param name="revision">Schema revision (see <see cref="Revision"/>). Defaults to <c>1</c>.</param>
    /// <param name="alias">Optional human-readable label used by the Workbench (see <see cref="Alias"/>). Falls back to the declaring type name when <c>null</c>.</param>
    public ArchetypeAttribute(ushort id, int revision = 1, string alias = null)
    {
        Id = id;
        Revision = revision;
        Alias = alias;
    }
}

/// <summary>
/// Marks a component struct as belonging to a named family. The Workbench Data Flow module groups components by family
/// at the L2 granularity ("Component-family"). When the attribute is absent, a server-side naming heuristic
/// (Spatial / Combat / AI / Inventory / Rendering / Networking / Input / Misc) classifies the component by its name.
/// The attribute always wins over the heuristic.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
[PublicAPI]
public sealed class ComponentFamilyAttribute : Attribute
{
    /// <summary>Family name that groups this component in the Workbench Data Flow view at the L2 ("Component-family") granularity.</summary>
    public string Name { get; }

    /// <summary>Assigns this component to the family named <paramref name="name"/>.</summary>
    /// <param name="name">Non-empty family name (see <see cref="Name"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <c>null</c> or empty.</exception>
    public ComponentFamilyAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }
}

/// <summary>
/// Controls whether a spatial-indexed component uses a static or dynamic R-Tree.
/// Static trees skip tick-fence updates entirely; dynamic trees use fat AABBs for movement hysteresis.
/// </summary>
[PublicAPI]
public enum SpatialMode : byte
{
    /// <summary>Entities move — the R-Tree stores fat (margin-expanded) AABBs for movement hysteresis and is refreshed at the tick fence.</summary>
    Dynamic = 0,

    /// <summary>Entities never move — the R-Tree skips tick-fence updates entirely.</summary>
    Static = 1,
}

/// <summary>Marks a spatial (AABB or bounding-sphere) component field for R-Tree indexing, enabling range and nearest-neighbor queries over its bounds.</summary>
[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class SpatialIndexAttribute : Attribute
{
    /// <summary>Fat-AABB expansion margin, in world units, added to each dynamic entry so small movements don't force a re-insert. Only meaningful for <see cref="SpatialMode.Dynamic"/>.</summary>
    public float Margin { get; }

    /// <summary>Cell size, in world units, for the coarse-grid broadphase occupancy filter. <c>0</c> (default) disables the filter — queries go straight to the tree.</summary>
    public float CellSize { get; }

    /// <summary>Whether the index is <see cref="SpatialMode.Dynamic"/> (default) or <see cref="SpatialMode.Static"/>.</summary>
    public SpatialMode Mode { get; set; } = SpatialMode.Dynamic;

    /// <summary>
    /// Archetype-level category bitmask used by the per-cell cluster spatial broadphase to skip entire clusters whose category does not intersect the
    /// query's category mask (issue #230 Phase 3). Defaults to <see cref="uint.MaxValue"/> — "accept every query" — so archetypes that don't set this
    /// remain queryable with any mask, including the default <c>uint.MaxValue</c> query mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Archetype-level, not per-entity.</b> Every entity in an archetype contributes the same <c>Category</c> value, so the per-cluster category mask
    /// is the OR of N identical values — effectively a constant for the archetype. This simplification makes the design-doc "incremental OR on spawn,
    /// recompute on destroy" invariants trivially satisfied: both are no-ops because the mask never changes.
    /// </para>
    /// <para>
    /// <b>Query semantics.</b> A cluster is admitted when <c>(clusterMask &amp; queryMask) != 0</c> — "any bit overlap".
    /// A query mask of <c>0</c> is a special sentinel that bypasses category filtering entirely (accepts all clusters regardless of their mask).
    /// Typical usage: assign distinct bit positions to archetype roles (Ants = <c>1 &lt;&lt; 0</c>,
    /// Food = <c>1 &lt;&lt; 1</c>, Enemies = <c>1 &lt;&lt; 2</c>) and query with the OR of the roles you want.
    /// </para>
    /// </remarks>
    public uint Category { get; set; } = uint.MaxValue;

    /// <summary>Marks a spatial field with the given fat-AABB <paramref name="margin"/> and optional broadphase <paramref name="cellSize"/>.</summary>
    /// <param name="margin">Fat-AABB expansion margin in world units (see <see cref="Margin"/>).</param>
    /// <param name="cellSize">Broadphase grid cell size in world units, or <c>0</c> to disable the coarse filter (see <see cref="CellSize"/>).</param>
    public SpatialIndexAttribute(float margin, float cellSize = 0f)
    {
        Margin = margin;
        CellSize = cellSize;
    }
}
