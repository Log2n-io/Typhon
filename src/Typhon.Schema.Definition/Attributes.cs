using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Typhon.Engine")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Schema.Definition;

[AttributeUsage(AttributeTargets.Struct)]
[PublicAPI]
public sealed class ComponentAttribute : Attribute
{
    public string Name { get; }
    public int Revision { get; }

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

    public ComponentAttribute(string name, int revision)
    {
        Name = name;
        Revision = revision;
    }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class FieldAttribute : Attribute
{
    public int? FieldId { get; set; }
    public string Name { get; set; }
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

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class IndexAttribute : Attribute
{
    public bool AllowMultiple { get; set; }

    /// <summary>
    /// Cascade action when the parent entity (referenced by an <see cref="EntityLink{T}"/> FK field) is deleted.
    /// Only applicable to indexed EntityLink fields. Default is <see cref="CascadeAction.None"/>.
    /// </summary>
    public CascadeAction OnParentDelete { get; set; } = CascadeAction.None;
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class ForeignKeyAttribute : Attribute
{
    public Type TargetComponentType { get; }

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
    public string Name { get; }

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
    Dynamic = 0,
    Static = 1,
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class SpatialIndexAttribute : Attribute
{
    public float Margin { get; }
    public float CellSize { get; }
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

    public SpatialIndexAttribute(float margin, float cellSize = 0f)
    {
        Margin = margin;
        CellSize = cellSize;
    }
}
